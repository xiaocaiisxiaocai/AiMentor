using AiMentor.Application;

namespace AiMentor.Infrastructure;

/// <summary>记录后台清理的动态健康状态，使持续故障能够让 readiness 失败关闭。</summary>
public sealed class MemoryRetentionHealthState(bool enabled)
{
    private int _consecutiveFailures;

    public bool Enabled { get; } = enabled;
    public bool IsReady => !Enabled || Volatile.Read(ref _consecutiveFailures) == 0;
    public int ConsecutiveFailures => Volatile.Read(ref _consecutiveFailures);

    public void MarkSuccess() => Interlocked.Exchange(ref _consecutiveFailures, 0);
    public void MarkFailure() => Interlocked.Increment(ref _consecutiveFailures);
}

/// <summary>
/// 把显式后台清理限制到支持分布式互斥的存储实现，并始终使用服务器时间。
/// 宿主可按固定周期调用；争锁失败是正常的无副作用结果。
/// </summary>
public sealed class MemoryRetentionService(IMemoryStore store, TimeProvider timeProvider) : IMemoryRetentionService
{
    /// <inheritdoc />
    public Task<MemoryPurgeResult> PurgeExpiredAsync(int maximumRowsPerTable,
        CancellationToken cancellationToken = default)
    {
        if (store is not IMemoryRetentionStore retentionStore)
            throw new InvalidOperationException("当前记忆存储不支持跨实例保留期清理。");
        return retentionStore.PurgeExpiredAsync(timeProvider.GetUtcNow(), maximumRowsPerTable, cancellationToken);
    }
}
