using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using AiMentor.Application;
using AiMentor.Domain;

namespace AiMentor.Infrastructure;

/// <summary>并发安全的单进程 Store；接口刻意不承诺跨进程或重启后的耐久性。</summary>
public sealed class InMemoryAtlasIncidentStore(TimeProvider timeProvider) : IAtlasIncidentStore
{
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    public Task CreateAsync(AtlasIncidentCheckpoint checkpoint, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_entries.TryAdd(checkpoint.RunId, new Entry(checkpoint)))
            throw Failure("ATLAS_RUN_CONFLICT", "排查运行标识冲突。", AtlasIncidentErrorKind.Conflict);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<AtlasIncidentCheckpoint>> ListAsync(AccessContext access, int limit,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (limit is < 1 or > 200) throw new ArgumentOutOfRangeException(nameof(limit));
        IReadOnlyList<AtlasIncidentCheckpoint> result = _entries.Values.Select(entry =>
            {
                lock (entry.Gate) return ExpireIfNeeded(entry);
            })
            .Where(item => item.Access.TenantId == access.TenantId && item.Access.SubjectId == access.SubjectId)
            .OrderByDescending(item => item.UpdatedAt).Take(limit).ToArray();
        return Task.FromResult(result);
    }

    public Task<AtlasIncidentCheckpoint?> GetAsync(string runId, AccessContext access,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_entries.TryGetValue(runId, out var entry)) return Task.FromResult<AtlasIncidentCheckpoint?>(null);
        lock (entry.Gate)
        {
            EnsureOwner(entry.Checkpoint, access);
            return Task.FromResult<AtlasIncidentCheckpoint?>(ExpireIfNeeded(entry));
        }
    }

    public Task<AtlasIncidentLeaseResult> TryAcquireAsync(string runId, AccessContext access, long expectedVersion,
        TimeSpan leaseDuration, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_entries.TryGetValue(runId, out var entry)) return Task.FromResult(new AtlasIncidentLeaseResult(false, null, null));
        lock (entry.Gate)
        {
            EnsureOwner(entry.Checkpoint, access);
            var checkpoint = ExpireIfNeeded(entry);
            if (checkpoint.Version != expectedVersion)
                throw Failure("ATLAS_VERSION_CONFLICT", "排查状态已被其他请求推进，请重新读取。", AtlasIncidentErrorKind.Conflict);
            if (checkpoint.Status is AtlasIncidentStatus.Cancelled or AtlasIncidentStatus.Expired)
                throw Failure("ATLAS_RUN_TERMINAL", "排查运行已经结束，不能继续推进。", AtlasIncidentErrorKind.Conflict);
            var now = timeProvider.GetUtcNow();
            if (entry.LeaseExpiresAt > now)
                throw Failure("ATLAS_RUN_BUSY", "排查运行正在由其他请求推进。", AtlasIncidentErrorKind.Conflict);
            entry.LeaseToken = Guid.NewGuid().ToString("N");
            entry.LeaseExpiresAt = now + leaseDuration;
            return Task.FromResult(new AtlasIncidentLeaseResult(true, entry.LeaseToken, checkpoint));
        }
    }

    public Task<AtlasIncidentCheckpoint> SaveAndReleaseAsync(AtlasIncidentCheckpoint checkpoint, string leaseToken,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_entries.TryGetValue(checkpoint.RunId, out var entry))
            throw Failure("ATLAS_RUN_NOT_FOUND", "没有找到排查运行。", AtlasIncidentErrorKind.NotFound);
        lock (entry.Gate)
        {
            if (!string.Equals(entry.LeaseToken, leaseToken, StringComparison.Ordinal)
                || entry.LeaseExpiresAt <= timeProvider.GetUtcNow())
                throw Failure("ATLAS_LEASE_LOST", "排查推进租约已失效。", AtlasIncidentErrorKind.Conflict);
            if (checkpoint.Version != entry.Checkpoint.Version + 1)
                throw Failure("ATLAS_VERSION_CONFLICT", "排查状态版本无效。", AtlasIncidentErrorKind.Conflict);
            entry.Checkpoint = checkpoint;
            entry.LeaseToken = null;
            entry.LeaseExpiresAt = default;
            return Task.FromResult(checkpoint);
        }
    }

    public Task<AtlasIncidentCheckpoint> CancelAsync(string runId, AccessContext access, long expectedVersion,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_entries.TryGetValue(runId, out var entry))
            throw Failure("ATLAS_RUN_NOT_FOUND", "没有找到排查运行。", AtlasIncidentErrorKind.NotFound);
        lock (entry.Gate)
        {
            EnsureOwner(entry.Checkpoint, access);
            var current = ExpireIfNeeded(entry);
            if (current.Version != expectedVersion)
                throw Failure("ATLAS_VERSION_CONFLICT", "排查状态已被其他请求推进，请重新读取。", AtlasIncidentErrorKind.Conflict);
            if (current.Status is AtlasIncidentStatus.Cancelled or AtlasIncidentStatus.Expired)
                return Task.FromResult(current);
            if (entry.LeaseExpiresAt > timeProvider.GetUtcNow())
                throw Failure("ATLAS_RUN_BUSY", "排查运行正在由其他请求推进。", AtlasIncidentErrorKind.Conflict);
            entry.Checkpoint = current with
            {
                Status = AtlasIncidentStatus.Cancelled,
                Version = current.Version + 1,
                UpdatedAt = timeProvider.GetUtcNow()
            };
            return Task.FromResult(entry.Checkpoint);
        }
    }

    private AtlasIncidentCheckpoint ExpireIfNeeded(Entry entry)
    {
        if (entry.Checkpoint.Status is not (AtlasIncidentStatus.Cancelled or AtlasIncidentStatus.Expired)
            && entry.Checkpoint.ExpiresAt <= timeProvider.GetUtcNow())
            entry.Checkpoint = entry.Checkpoint with
            {
                Status = AtlasIncidentStatus.Expired,
                Version = entry.Checkpoint.Version + 1,
                UpdatedAt = timeProvider.GetUtcNow()
            };
        return entry.Checkpoint;
    }

    private static void EnsureOwner(AtlasIncidentCheckpoint checkpoint, AccessContext access)
    {
        if (!string.Equals(checkpoint.Access.TenantId, access.TenantId, StringComparison.Ordinal)
            || !string.Equals(checkpoint.Access.SubjectId, access.SubjectId, StringComparison.Ordinal))
            throw Failure("ATLAS_RUN_FORBIDDEN", "只有原始调用者可以访问排查运行。", AtlasIncidentErrorKind.Forbidden);
    }

    private static AtlasIncidentWorkflowException Failure(string code, string message, AtlasIncidentErrorKind kind) =>
        new(code, message, kind);

    private sealed class Entry(AtlasIncidentCheckpoint checkpoint)
    {
        public object Gate { get; } = new();
        public AtlasIncidentCheckpoint Checkpoint { get; set; } = checkpoint;
        public string? LeaseToken { get; set; }
        public DateTimeOffset LeaseExpiresAt { get; set; }
    }
}

/// <summary>按 BK-RUN-001 v2.2 固定顺序执行 AtlasID 登录异常排查。</summary>
public sealed partial class AtlasIncidentWorkflow(
    IAtlasIncidentStore store,
    AtlasIncidentWorkflowOptions options,
    TimeProvider timeProvider) : IAtlasIncidentWorkflow
{
    public async Task<AtlasIncidentCheckpoint> StartAsync(AtlasIncidentInput input, AccessContext access,
        CancellationToken cancellationToken = default)
    {
        Validate(input, access);
        var now = timeProvider.GetUtcNow();
        var safe = Sanitize(input);
        var required = Required(safe);
        var checkpoint = new AtlasIncidentCheckpoint(Guid.NewGuid().ToString("N"), access, options.RunbookId,
            options.RunbookVersion, required.Count == 0 ? AtlasIncidentStatus.InspectingClock : AtlasIncidentStatus.RequiredInputs,
            safe, required, [], null, 1, now, now, now + options.Retention);
        checkpoint = Advance(checkpoint);
        await store.CreateAsync(checkpoint, cancellationToken);
        return checkpoint;
    }

    public async Task<AtlasIncidentCheckpoint> GetAsync(string runId, AccessContext access,
        CancellationToken cancellationToken = default) =>
        await store.GetAsync(RequireRunId(runId), access, cancellationToken)
        ?? throw Failure("ATLAS_RUN_NOT_FOUND", "没有找到排查运行。", AtlasIncidentErrorKind.NotFound);

    public async Task<AtlasIncidentCheckpoint> ResumeAsync(string runId, long expectedVersion,
        AtlasIncidentInput input, AccessContext access, CancellationToken cancellationToken = default)
    {
        Validate(input, access, allowEmpty: true);
        if (expectedVersion <= 0)
            throw Failure("ATLAS_VERSION_INVALID", "ExpectedVersion 必须大于 0。", AtlasIncidentErrorKind.Validation);
        var snapshot = await GetAsync(runId, access, cancellationToken);
        // 版本不匹配时不获取租约，避免把不可恢复的旧检查点暂时锁住。
        if (snapshot.RunbookId != options.RunbookId || snapshot.RunbookVersion != options.RunbookVersion)
            throw Failure("ATLAS_RUNBOOK_VERSION_MISMATCH", "排查手册版本已变化，请创建新的排查运行。", AtlasIncidentErrorKind.Conflict);
        var lease = await store.TryAcquireAsync(RequireRunId(runId), access, expectedVersion,
            options.ProgressLeaseDuration, cancellationToken);
        if (!lease.Acquired || lease.Checkpoint is null || lease.LeaseToken is null)
            throw Failure("ATLAS_RUN_NOT_FOUND", "没有找到排查运行。", AtlasIncidentErrorKind.NotFound);
        var current = lease.Checkpoint;
        var merged = Merge(current.SafeInput, Sanitize(input));
        var required = Required(merged);
        var updated = current with
        {
            SafeInput = merged,
            RequiredInputs = required,
            Status = required.Count == 0 ? AtlasIncidentStatus.InspectingClock : AtlasIncidentStatus.RequiredInputs,
            Version = current.Version + 1,
            UpdatedAt = timeProvider.GetUtcNow()
        };
        updated = Advance(updated);
        return await store.SaveAndReleaseAsync(updated, lease.LeaseToken, cancellationToken);
    }

    public Task<AtlasIncidentCheckpoint> CancelAsync(string runId, long expectedVersion, AccessContext access,
        CancellationToken cancellationToken = default) =>
        store.CancelAsync(RequireRunId(runId), access, expectedVersion, cancellationToken);

    private static AtlasIncidentCheckpoint Advance(AtlasIncidentCheckpoint checkpoint)
    {
        if (checkpoint.RequiredInputs.Count > 0) return checkpoint;
        var input = checkpoint.SafeInput;
        var findings = new List<AtlasIncidentFinding>();
        if (Math.Abs(input.NodeUtcOffsetSeconds!.Value) > 60)
            findings.Add(new("CLOCK_SKEW", "服务节点 UTC 时间偏差超过允许范围。",
                "优先恢复节点 NTP 同步，再重新验证 Token 时间窗口。", 1));
        if (input.TokenMetadata!.NotBefore > input.ObservedAt)
            findings.Add(new("TOKEN_NOT_YET_VALID", "Token 在观测时刻尚未生效。",
                "先核对节点 UTC 时间与 NTP 状态，再核对签发时间。", 2));
        if (input.TokenMetadata.ExpiresAt <= input.ObservedAt)
            findings.Add(new("TOKEN_EXPIRED", "Token 在观测时刻已经过期。", "重新获取 Token，并核对客户端刷新流程。", 2));
        if (input.JwksCacheStale == true)
            findings.Add(new("JWKS_CACHE_STALE", "节点 JWKS 缓存可能过期。", "受控刷新该节点 JWKS 缓存并复验签名。", 3));
        if (input.TokenMetadata.SignatureValid == false)
            findings.Add(new("SIGNATURE_VALIDATION_FAILED", "Token 签名验证失败。",
                "检查对应 kid、签名证书和 JWKS 缓存；不能仅凭此结论轮换全部密钥。", 3));
        if (input.TokenMetadata.IssuerMatches == false)
            findings.Add(new("ISSUER_MISMATCH", "Token Issuer 与服务配置不匹配。", "核对该区域身份配置和最近变更。", 4));
        if (input.TokenMetadata.AudienceMatches == false)
            findings.Add(new("AUDIENCE_MISMATCH", "Token Audience 与服务配置不匹配。", "核对客户端和服务 Audience 配置。", 4));
        if (input.RecentIdentityConfigurationChange == true)
            findings.Add(new("RECENT_IDENTITY_CHANGE", "时间范围内存在身份配置或证书变更。", "关联变更记录并核对影响范围。", 5));
        if (findings.Count == 0)
            findings.Add(new("NO_CONFIRMED_CAUSE", "当前元数据不足以确认单一根因。", "保留现状并补充节点日志与变更记录。", 9));

        var action = NormalizeAction(input.ProposedAction);
        return checkpoint with
        {
            Status = action is null ? AtlasIncidentStatus.DiagnosisReady : AtlasIncidentStatus.AwaitingActionApproval,
            Findings = findings.OrderBy(item => item.Priority).ToArray(),
            ProposedAction = action,
            SafeInput = input with { ProposedAction = null }
        };
    }

    private static List<string> Required(SafeAtlasIncidentInput input)
    {
        var required = new List<string>();
        if (string.IsNullOrWhiteSpace(input.Region)) required.Add("region");
        if (string.IsNullOrWhiteSpace(input.Node)) required.Add("node");
        if (input.ObservedAt is null) required.Add("observedAt");
        if (input.TokenMetadata?.ExpiresAt is null) required.Add("tokenMetadata.expiresAt");
        if (input.TokenMetadata?.NotBefore is null) required.Add("tokenMetadata.notBefore");
        if (input.TokenMetadata?.IssuerMatches is null) required.Add("tokenMetadata.issuerMatches");
        if (input.TokenMetadata?.AudienceMatches is null) required.Add("tokenMetadata.audienceMatches");
        if (input.TokenMetadata?.SignatureValid is null) required.Add("tokenMetadata.signatureValid");
        if (input.NodeUtcOffsetSeconds is null) required.Add("nodeUtcOffsetSeconds");
        if (input.JwksCacheStale is null) required.Add("jwksCacheStale");
        if (input.RecentIdentityConfigurationChange is null) required.Add("recentIdentityConfigurationChange");
        return required;
    }

    private static SafeAtlasIncidentInput Merge(SafeAtlasIncidentInput old, SafeAtlasIncidentInput patch) => new(
        patch.Region ?? old.Region, patch.Node ?? old.Node, patch.ObservedAt ?? old.ObservedAt,
        patch.TokenMetadata is null ? old.TokenMetadata : new AtlasTokenMetadata(
            patch.TokenMetadata.ExpiresAt ?? old.TokenMetadata?.ExpiresAt,
            patch.TokenMetadata.NotBefore ?? old.TokenMetadata?.NotBefore,
            patch.TokenMetadata.IssuerMatches ?? old.TokenMetadata?.IssuerMatches,
            patch.TokenMetadata.AudienceMatches ?? old.TokenMetadata?.AudienceMatches,
            patch.TokenMetadata.SignatureValid ?? old.TokenMetadata?.SignatureValid),
        patch.NodeUtcOffsetSeconds ?? old.NodeUtcOffsetSeconds,
        patch.JwksCacheStale ?? old.JwksCacheStale,
        patch.RecentIdentityConfigurationChange ?? old.RecentIdentityConfigurationChange,
        patch.ProposedAction ?? old.ProposedAction);

    private static SafeAtlasIncidentInput Sanitize(AtlasIncidentInput input) => new(
        input.Region?.Trim(), input.Node?.Trim(), input.ObservedAt, input.TokenMetadata,
        input.NodeUtcOffsetSeconds, input.JwksCacheStale, input.RecentIdentityConfigurationChange,
        input.ProposedAction?.Trim());

    private static void Validate(AtlasIncidentInput input, AccessContext access, bool allowEmpty = false)
    {
        if (string.IsNullOrWhiteSpace(access.TenantId) || string.IsNullOrWhiteSpace(access.SubjectId))
            throw Failure("ATLAS_IDENTITY_INVALID", "缺少有效租户或用户身份。", AtlasIncidentErrorKind.Validation);
        if (!string.IsNullOrWhiteSpace(input.RawToken))
            throw Failure("ATLAS_RAW_TOKEN_FORBIDDEN", "不得提交原始 Token，只能提交脱敏元数据。", AtlasIncidentErrorKind.Validation);
        if (RawTokenPattern().IsMatch($"{input.Region} {input.Node} {input.ProposedAction}"))
            throw Failure("ATLAS_RAW_TOKEN_FORBIDDEN", "排查字段中检测到疑似原始 Token，只能提交脱敏元数据。", AtlasIncidentErrorKind.Validation);
        if (!allowEmpty && input == new AtlasIncidentInput())
            throw Failure("ATLAS_INPUT_EMPTY", "排查输入不能为空。", AtlasIncidentErrorKind.Validation);
        if ((input.Region?.Length ?? 0) > 64 || (input.Node?.Length ?? 0) > 128
            || (input.ProposedAction?.Length ?? 0) > 256)
            throw Failure("ATLAS_INPUT_TOO_LONG", "排查输入超过长度限制。", AtlasIncidentErrorKind.Validation);
    }

    private static string? NormalizeAction(string? action)
    {
        if (string.IsNullOrWhiteSpace(action)) return null;
        // 这里只生成待审批动作，状态机本身永不执行配置、证书、重启或缓存修改。
        return action.Trim();
    }

    private static string RequireRunId(string runId)
    {
        var normalized = runId?.Trim();
        if (normalized is not { Length: > 0 and <= 128 } || !RunIdPattern().IsMatch(normalized))
            throw Failure("ATLAS_RUN_ID_INVALID", "排查运行标识无效。", AtlasIncidentErrorKind.Validation);
        return normalized;
    }

    private static AtlasIncidentWorkflowException Failure(string code, string message, AtlasIncidentErrorKind kind) =>
        new(code, message, kind);

    [GeneratedRegex("^[a-zA-Z0-9._-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex RunIdPattern();

    [GeneratedRegex(@"\b[A-Za-z0-9_-]{12,}\.[A-Za-z0-9_-]{12,}\.[A-Za-z0-9_-]{12,}\b",
        RegexOptions.CultureInvariant)]
    private static partial Regex RawTokenPattern();
}
