using System.Text.RegularExpressions;
using AiMentor.Application;
using AiMentor.Domain;

namespace AiMentor.Infrastructure;

/// <summary>供单元测试使用的进程内记忆存储，保持与持久化实现相同的所有权语义。</summary>
public sealed class InMemoryMemoryStore : IMemoryStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, MemoryProposal> _proposals = new(StringComparer.Ordinal);
    private readonly Dictionary<string, MemoryRecord> _memories = new(StringComparer.Ordinal);

    public Task SaveProposalAsync(MemoryProposal proposal, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!_proposals.TryAdd(proposal.Id, proposal))
                throw new InvalidOperationException("记忆提案标识发生冲突。");
        }
        return Task.CompletedTask;
    }

    public Task<MemoryStoreResult<MemoryRecord>> ApproveAsync(string proposalId, AccessContext access, DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!_proposals.TryGetValue(proposalId, out var proposal) || !OwnedBy(proposal.TenantId, proposal.SubjectId, access))
                return Task.FromResult(new MemoryStoreResult<MemoryRecord>(MemoryStoreStatus.NotFound));
            if (proposal.Status != MemoryProposalStatus.PendingApproval)
                return Task.FromResult(new MemoryStoreResult<MemoryRecord>(MemoryStoreStatus.Conflict));
            if (proposal.ApprovalExpiresAt <= now || proposal.MemoryExpiresAt <= now)
                return Task.FromResult(new MemoryStoreResult<MemoryRecord>(MemoryStoreStatus.Expired));
            if (_memories.Values.Any(item => item.ExpiresAt > now
                && OwnedBy(item.TenantId, item.SubjectId, access)
                && item.Scope == proposal.Scope
                && string.Equals(item.SessionId, proposal.SessionId, StringComparison.Ordinal)
                && string.Equals(item.Key, proposal.Key, StringComparison.OrdinalIgnoreCase)))
                return Task.FromResult(new MemoryStoreResult<MemoryRecord>(MemoryStoreStatus.AlreadyExists));

            var memory = new MemoryRecord(Guid.NewGuid().ToString("N"), proposal.TenantId, proposal.SubjectId,
                proposal.Scope, proposal.SessionId, proposal.Key, proposal.Value, 1, now, now, proposal.MemoryExpiresAt);
            _memories.Add(memory.Id, memory);
            _proposals[proposal.Id] = proposal with { Status = MemoryProposalStatus.Approved };
            return Task.FromResult(new MemoryStoreResult<MemoryRecord>(MemoryStoreStatus.Success, memory));
        }
    }

    public Task<IReadOnlyList<MemoryRecord>> ListActiveAsync(AccessContext access, MemoryScope? scope, string? sessionId,
        DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var expiredIds = _memories.Values.Where(item => item.ExpiresAt <= now).Select(item => item.Id).ToArray();
            foreach (var id in expiredIds) _memories.Remove(id);
            IReadOnlyList<MemoryRecord> result = _memories.Values
                .Where(item => OwnedBy(item.TenantId, item.SubjectId, access))
                .Where(item => scope is null || item.Scope == scope)
                .Where(item => sessionId is null || string.Equals(item.SessionId, sessionId, StringComparison.Ordinal))
                .OrderBy(item => item.Scope)
                .ThenBy(item => item.Key, StringComparer.Ordinal)
                .ToArray();
            return Task.FromResult(result);
        }
    }

    public Task<MemoryStoreResult<MemoryRecord>> UpdateAsync(string memoryId, AccessContext access, int expectedVersion,
        string value, DateTimeOffset? expiresAt, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!_memories.TryGetValue(memoryId, out var memory) || !OwnedBy(memory.TenantId, memory.SubjectId, access)
                || memory.ExpiresAt <= now)
                return Task.FromResult(new MemoryStoreResult<MemoryRecord>(MemoryStoreStatus.NotFound));
            if (memory.Version != expectedVersion)
                return Task.FromResult(new MemoryStoreResult<MemoryRecord>(MemoryStoreStatus.Conflict));
            if (expiresAt > memory.ExpiresAt)
                return Task.FromResult(new MemoryStoreResult<MemoryRecord>(MemoryStoreStatus.RetentionExceeded));

            var updated = memory with
            {
                Value = value,
                Version = memory.Version + 1,
                UpdatedAt = now,
                ExpiresAt = expiresAt ?? memory.ExpiresAt
            };
            _memories[memoryId] = updated;
            return Task.FromResult(new MemoryStoreResult<MemoryRecord>(MemoryStoreStatus.Success, updated));
        }
    }

    public Task<MemoryStoreResult<bool>> DeleteAsync(string memoryId, AccessContext access, int expectedVersion,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!_memories.TryGetValue(memoryId, out var memory) || !OwnedBy(memory.TenantId, memory.SubjectId, access))
                return Task.FromResult(new MemoryStoreResult<bool>(MemoryStoreStatus.NotFound));
            if (memory.Version != expectedVersion)
                return Task.FromResult(new MemoryStoreResult<bool>(MemoryStoreStatus.Conflict));
            _memories.Remove(memoryId);
            return Task.FromResult(new MemoryStoreResult<bool>(MemoryStoreStatus.Success, true));
        }
    }

    /// <inheritdoc />
    public Task<MemoryTargetState> ProbeTargetStateAsync(string memoryId, AccessContext access, int expectedVersion,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!_memories.TryGetValue(memoryId, out var memory))
                return Task.FromResult(MemoryTargetState.Absent);
            if (!OwnedBy(memory.TenantId, memory.SubjectId, access))
                return Task.FromResult(MemoryTargetState.Inaccessible);
            return Task.FromResult(memory.Version == expectedVersion
                ? MemoryTargetState.PresentAtExpectedVersion
                : MemoryTargetState.PresentAtDifferentVersion);
        }
    }

    /// <inheritdoc />
    public Task<MemoryStoreResult<MemoryRecord>> RestoreCompensationAsync(string memoryId, AccessContext access,
        int expectedVersion, string previousValue, DateTimeOffset previousExpiresAt, DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!_memories.TryGetValue(memoryId, out var memory)
                || !OwnedBy(memory.TenantId, memory.SubjectId, access))
                return Task.FromResult(new MemoryStoreResult<MemoryRecord>(MemoryStoreStatus.NotFound));
            if (memory.Version != expectedVersion)
                return Task.FromResult(new MemoryStoreResult<MemoryRecord>(MemoryStoreStatus.Conflict));
            // 该专用路径只能恢复服务器加密快照，允许回到比正向更正更晚的原期限。
            var restored = memory with
            {
                Value = previousValue,
                Version = memory.Version + 1,
                UpdatedAt = now,
                ExpiresAt = previousExpiresAt
            };
            _memories[memoryId] = restored;
            return Task.FromResult(new MemoryStoreResult<MemoryRecord>(MemoryStoreStatus.Success, restored));
        }
    }

    /// <inheritdoc />
    public Task<MemoryCorrectionCompensationState> ProbeCorrectionCompensationAsync(string memoryId,
        AccessContext access, int expectedVersionAfterForward, string previousValue,
        DateTimeOffset previousExpiresAt, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!_memories.TryGetValue(memoryId, out var memory)
                || !OwnedBy(memory.TenantId, memory.SubjectId, access))
                return Task.FromResult(MemoryCorrectionCompensationState.Inaccessible);
            if (memory.Version == expectedVersionAfterForward)
                return Task.FromResult(MemoryCorrectionCompensationState.NotApplied);
            return Task.FromResult(memory.Version == expectedVersionAfterForward + 1
                && string.Equals(memory.Value, previousValue, StringComparison.Ordinal)
                && memory.ExpiresAt.Equals(previousExpiresAt)
                    ? MemoryCorrectionCompensationState.Applied
                    : MemoryCorrectionCompensationState.Changed);
        }
    }

    private static bool OwnedBy(string tenantId, string subjectId, AccessContext access) =>
        string.Equals(tenantId, access.TenantId, StringComparison.OrdinalIgnoreCase)
        && string.Equals(subjectId, access.SubjectId, StringComparison.Ordinal);
}

/// <summary>阻止凭证、个人敏感标识和提示词注入内容进入长期记忆。</summary>
public sealed partial class RuleBasedMemoryContentSafetyService : IMemoryContentSafetyService
{
    public SafetyDecision Review(string key, string value)
    {
        var content = $"{key}\n{value}";
        if (SecretValue().IsMatch(content))
            return Refuse("MEMORY_SECRET_FORBIDDEN", "记忆不得保存密码、令牌、私钥或其他可用凭证。");
        if (SensitiveIdentifier().IsMatch(content))
            return Refuse("MEMORY_SENSITIVE_IDENTIFIER_FORBIDDEN", "记忆不得保存身份证号或银行卡号等高风险标识符。");
        if (EmbeddedInstruction().IsMatch(content))
            return Refuse("MEMORY_PROMPT_INJECTION", "记忆内容疑似包含用于改变系统行为的恶意指令。");
        return new SafetyDecision(SafetyAction.Allow, "MEMORY_CONTENT_SAFE", "记忆内容通过安全审核。");
    }

    private static SafetyDecision Refuse(string code, string message) => new(SafetyAction.Refuse, code, message);

    [GeneratedRegex(@"-----BEGIN (?:RSA |EC )?PRIVATE KEY-----|\bAKIA[0-9A-Z]{16}\b|\b(?:sk|pk)_(?:live|test)_[a-z0-9]{12,}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SecretValue();

    [GeneratedRegex(@"(?<!\d)(?:\d{17}[0-9Xx]|\d{16,19})(?!\d)", RegexOptions.CultureInvariant)]
    private static partial Regex SensitiveIdentifier();

    [GeneratedRegex(@"(?is)<\s*(?:system|developer|assistant)\b|(?:忽略|无视|绕过).{0,40}(?:系统|开发者|之前|以上).{0,30}(?:指令|规则)|ignore.{0,40}(?:system|developer|previous).{0,30}(?:instruction|message)", RegexOptions.CultureInvariant)]
    private static partial Regex EmbeddedInstruction();
}
