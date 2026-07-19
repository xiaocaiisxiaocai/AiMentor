using AiMentor.Application;
using AiMentor.Infrastructure;

namespace AiMentor.Api;

/// <summary>配置跨副本互斥的过期记忆后台清理周期与单次上限。</summary>
public sealed class MemoryRetentionBackgroundOptions
{
    public TimeSpan Interval { get; init; } = TimeSpan.FromMinutes(15);
    public int MaximumRowsPerTable { get; init; } = 1_000;
}

/// <summary>周期触发有界清理；真正的跨实例单胜者由 SQL 应用锁保证。</summary>
public sealed class MemoryRetentionBackgroundService(
    IMemoryRetentionService retentionService,
    MemoryRetentionBackgroundOptions options,
    MemoryRetentionHealthState healthState,
    TimeProvider timeProvider,
    ILogger<MemoryRetentionBackgroundService> logger) : BackgroundService
{
    private static readonly Action<ILogger, int, int, Exception?> PurgeCompleted =
        LoggerMessage.Define<int, int>(LogLevel.Information, new EventId(2101, "MemoryRetentionCompleted"),
            "记忆保留期清理完成：提案 {ProposalsDeleted}，正式记忆 {MemoriesDeleted}。");
    private static readonly Action<ILogger, Exception?> PurgeFailed =
        LoggerMessage.Define(LogLevel.Error, new EventId(2102, "MemoryRetentionFailed"),
            "记忆保留期后台清理失败，将在下一周期重试。");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await PurgeOnceAsync(stoppingToken);
        using var timer = new PeriodicTimer(options.Interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
            await PurgeOnceAsync(stoppingToken);
    }

    private async Task PurgeOnceAsync(CancellationToken cancellationToken)
    {
        var startedAt = timeProvider.GetTimestamp();
        try
        {
            var result = await retentionService.PurgeExpiredAsync(options.MaximumRowsPerTable, cancellationToken);
            // 争锁失败只在读到数据库中真实的成功提交时间后恢复健康，不能用一次空转刷新新鲜度。
            if (result.LastCompletedAt is { } completedAt)
                healthState.MarkSuccess(completedAt);
            AiMentorTelemetry.RecordMemoryRetention(result.LockAcquired ? "completed" : "lock_not_acquired",
                result.ProposalsDeleted, result.MemoriesDeleted, timeProvider.GetElapsedTime(startedAt),
                result.LastCompletedAt, result.MonitoringStartedAt);
            if (result.LockAcquired && (result.ProposalsDeleted > 0 || result.MemoriesDeleted > 0))
                PurgeCompleted(logger, result.ProposalsDeleted, result.MemoriesDeleted, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 宿主关闭时的取消属于正常生命周期，不记录为故障。
        }
        catch (Exception exception)
        {
            healthState.MarkFailure();
            AiMentorTelemetry.RecordMemoryRetention("failed", 0, 0, timeProvider.GetElapsedTime(startedAt));
            PurgeFailed(logger, exception);
        }
    }
}
