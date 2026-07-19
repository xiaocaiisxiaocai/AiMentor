using AiMentor.Application;

namespace AiMentor.Infrastructure;

/// <summary>记录后台清理的动态健康状态，使持续故障或长期未完成能够让 readiness 失败关闭。</summary>
public sealed class MemoryRetentionHealthState
{
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _maximumStaleness;
    private readonly long _initializedUtcTicks;
    private int _consecutiveFailures;
    private long _lastSuccessUtcTicks;

    public MemoryRetentionHealthState(bool enabled, TimeProvider? timeProvider = null,
        TimeSpan? maximumStaleness = null)
    {
        Enabled = enabled;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _maximumStaleness = maximumStaleness ?? TimeSpan.FromMinutes(30);
        if (_maximumStaleness <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(maximumStaleness), "最大陈旧时间必须大于零。");
        _initializedUtcTicks = _timeProvider.GetUtcNow().UtcTicks;
    }

    public bool Enabled { get; }
    public bool IsReady
    {
        get
        {
            if (!Enabled) return true;
            if (Volatile.Read(ref _consecutiveFailures) != 0) return false;
            return !IsStale;
        }
    }
    public bool IsStale
    {
        get
        {
            if (!Enabled) return false;
            var lastSuccess = Volatile.Read(ref _lastSuccessUtcTicks);
            var reference = lastSuccess == 0 ? _initializedUtcTicks : lastSuccess;
            return _timeProvider.GetUtcNow().UtcTicks - reference > _maximumStaleness.Ticks;
        }
    }
    public int ConsecutiveFailures => Volatile.Read(ref _consecutiveFailures);
    public DateTimeOffset? LastSuccessAt
    {
        get
        {
            var ticks = Volatile.Read(ref _lastSuccessUtcTicks);
            return ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
        }
    }

    public void MarkSuccess(DateTimeOffset? completedAt = null)
    {
        var now = _timeProvider.GetUtcNow();
        if (completedAt is { } invalidFuture && invalidFuture > now.AddMinutes(1))
        {
            // 明显的数据库/节点时钟漂移会破坏保留期判断，必须失败关闭而非每轮刷新本机时间。
            MarkFailure();
            return;
        }
        // 可接受的亚分钟时钟误差最多按本机当前时刻记账，待时钟追平后会自然按共享时间老化。
        var observedAt = completedAt is { } value && value < now ? value : now;
        Interlocked.Exchange(ref _lastSuccessUtcTicks, observedAt.UtcTicks);
        Interlocked.Exchange(ref _consecutiveFailures, 0);
    }

    public void MarkFailure() => Interlocked.Increment(ref _consecutiveFailures);
}

/// <summary>
/// 把显式后台清理限制到支持分布式互斥的存储实现，并始终使用服务器时间。
/// 宿主可按固定周期调用；争锁失败是正常的无副作用结果。
/// </summary>
public sealed class MemoryRetentionService(IMemoryStore store) : IMemoryRetentionService
{
    /// <inheritdoc />
    public Task<MemoryPurgeResult> PurgeExpiredAsync(int maximumRowsPerTable,
        CancellationToken cancellationToken = default)
    {
        if (store is not IMemoryRetentionStore retentionStore)
            throw new InvalidOperationException("当前记忆存储不支持跨实例保留期清理。");
        return retentionStore.PurgeExpiredAsync(maximumRowsPerTable, cancellationToken);
    }
}
