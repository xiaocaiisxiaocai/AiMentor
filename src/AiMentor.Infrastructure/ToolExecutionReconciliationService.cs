using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AiMentor.Application;
using AiMentor.Domain;

namespace AiMentor.Infrastructure;

/// <summary>实施租户、角色和参数指纹门禁后，提供只读结果不确定查询与目标状态探测。</summary>
public sealed class ToolExecutionReconciliationService : IToolExecutionReconciliationService
{
    private const string CursorSort = "updated-desc/execution-key-bin2-asc";
    private readonly IToolExecutionLedger _ledger;
    private readonly ITraceSink _traceSink;
    private readonly ToolExecutionReconciliationOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly Dictionary<string, IToolOutcomeProbe> _probes;
    private readonly byte[] _cursorSigningKey;

    public ToolExecutionReconciliationService(IToolExecutionLedger ledger, ITraceSink traceSink,
        ToolExecutionReconciliationOptions options, TimeProvider timeProvider, IEnumerable<IToolOutcomeProbe> probes)
    {
        _ledger = ledger;
        _traceSink = traceSink;
        _options = options;
        _timeProvider = timeProvider;
        _probes = probes.ToDictionary(probe => probe.ToolName, StringComparer.OrdinalIgnoreCase);
        if (_options.MaximumPageSize <= 0 || _options.MaximumOperationsScan < _options.MaximumPageSize)
            throw new ArgumentOutOfRangeException(nameof(options), "对账分页和运营扫描上限配置无效。");
        if (_options.CursorSigningKey is { Length: < 32 })
            throw new ArgumentException("对账游标签名密钥不得少于 32 字节。", nameof(options));
        // 开发与单测未显式配置时使用进程随机密钥，避免任何可伪造的固定后备密钥。
        _cursorSigningKey = _options.CursorSigningKey?.ToArray() ?? RandomNumberGenerator.GetBytes(32);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<OutcomeUnknownToolExecution>> ListOutcomeUnknownAsync(
        AccessContext access, int limit, CancellationToken cancellationToken = default)
        => (await ListOutcomeUnknownPageAsync(access, limit, null, cancellationToken)).Items;

    /// <inheritdoc />
    public async Task<OutcomeUnknownToolExecutionPage> ListOutcomeUnknownPageAsync(
        AccessContext access, int limit, string? cursor = null, CancellationToken cancellationToken = default)
    {
        EnsureAuthorized(access);
        if (limit <= 0 || limit > _options.MaximumPageSize)
            throw Failure("TOOL_RECONCILIATION_LIMIT_INVALID",
                $"limit 必须在 1 到 {_options.MaximumPageSize} 之间。",
                ToolExecutionReconciliationErrorKind.Validation);

        OutcomeUnknownToolExecutionPageKey? after = null;
        if (cursor is not null)
        {
            try
            {
                after = DecodeCursor(cursor, access.TenantId);
            }
            catch (Exception exception) when (exception is FormatException or JsonException
                                               or ArgumentOutOfRangeException)
            {
                throw Failure("TOOL_RECONCILIATION_CURSOR_INVALID", "cursor 格式或签名无效。",
                    ToolExecutionReconciliationErrorKind.Validation);
            }
        }

        var ledgerPage = await _ledger.ListOutcomeUnknownPageAsync(
            access.TenantId, limit, after, cancellationToken);
        var nextCursor = ledgerPage.NextKey is null
            ? null
            : EncodeCursor(ledgerPage.NextKey, access.TenantId);
        await TraceAsync(access, "tool.execution.reconciliation.list", "ok",
            new Dictionary<string, object?>
            {
                ["count"] = ledgerPage.Items.Count,
                ["hasNext"] = nextCursor is not null
            }, cancellationToken);
        return new OutcomeUnknownToolExecutionPage(ledgerPage.Items, nextCursor);
    }

    /// <inheritdoc />
    public async Task<ToolOutcomeProbeResult> ProbeOutcomeAsync(AccessContext access, string executionKey,
        JsonElement arguments, CancellationToken cancellationToken = default)
    {
        EnsureAuthorized(access);
        if (string.IsNullOrWhiteSpace(executionKey) || executionKey.Length != 64
            || !executionKey.All(Uri.IsHexDigit))
            throw Failure("TOOL_RECONCILIATION_EXECUTION_KEY_INVALID", "executionKey 格式无效。",
                ToolExecutionReconciliationErrorKind.Validation);
        if (arguments.ValueKind != JsonValueKind.Object)
            throw Failure("TOOL_RECONCILIATION_ARGUMENTS_INVALID", "对账参数必须是 JSON 对象。",
                ToolExecutionReconciliationErrorKind.Validation);

        var detail = await _ledger.GetOutcomeUnknownAsync(access.TenantId, executionKey.ToUpperInvariant(),
            cancellationToken) ?? throw Failure("TOOL_RECONCILIATION_NOT_FOUND",
                "当前租户不存在该结果不确定记录。", ToolExecutionReconciliationErrorKind.NotFound);
        if (!_probes.TryGetValue(detail.Execution.ToolName, out var probe))
            throw Failure("TOOL_OUTCOME_PROBE_NOT_SUPPORTED", "该工具尚未提供目标状态探测器。",
                ToolExecutionReconciliationErrorKind.Validation);

        var owner = AccessContext.Create(detail.Execution.TenantId, detail.Execution.SubjectId, []);
        var fingerprint = JsonArgumentFingerprint.Create(detail.Execution.ToolName, arguments, owner);
        if (!FixedEquals(fingerprint, detail.RequestFingerprint))
            throw Failure("TOOL_RECONCILIATION_ARGUMENTS_MISMATCH",
                "对账参数与原始工具调用不匹配。", ToolExecutionReconciliationErrorKind.Forbidden);

        var result = await probe.ProbeAsync(detail.Execution, arguments, cancellationToken);
        await TraceAsync(access, "tool.execution.reconciliation.probe", result.State.ToString(),
            new Dictionary<string, object?>
            {
                ["executionKey"] = executionKey,
                ["tool"] = detail.Execution.ToolName,
                ["code"] = result.Code
            }, cancellationToken);
        return result;
    }

    /// <inheritdoc />
    public async Task<ToolReconciliationReviewResult> ReviewOutcomeAsync(AccessContext access, string executionKey,
        JsonElement arguments, bool confirmed, string reason, CancellationToken cancellationToken = default)
    {
        EnsureAuthorized(access);
        if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length > 500)
            throw Failure("TOOL_RECONCILIATION_REASON_INVALID", "裁决理由必须为 1 到 500 个字符。",
                ToolExecutionReconciliationErrorKind.Validation);
        var evidence = await ProbeOutcomeAsync(access, executionKey, arguments, cancellationToken);
        if (confirmed && evidence.State == ToolOutcomeProbeState.Indeterminate)
            throw Failure("TOOL_RECONCILIATION_EVIDENCE_INDETERMINATE",
                "不确定证据不能用于结案或授权重试。", ToolExecutionReconciliationErrorKind.Validation);
        var review = new ToolReconciliationReview(evidence.ExecutionKey, access.TenantId, access.SubjectId,
            evidence.State, evidence.Code, evidence.ObservedAt,
            evidence.ObservedAt.Add(_options.EvidenceLifetime), confirmed,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(reason.Trim()))));
        var result = await _ledger.SubmitReconciliationReviewAsync(review, cancellationToken);
        await TraceAsync(access, "tool.execution.reconciliation.review", result.Status.ToString(),
            new Dictionary<string, object?>
            {
                ["executionKey"] = executionKey,
                ["evidenceState"] = evidence.State.ToString(),
                ["confirmed"] = confirmed
            }, cancellationToken);
        return result;
    }

    private void EnsureAuthorized(AccessContext access)
    {
        if (string.IsNullOrWhiteSpace(access.TenantId) || string.IsNullOrWhiteSpace(access.SubjectId))
            throw Failure("TOOL_RECONCILIATION_ACCESS_INVALID", "对账访问上下文无效。",
                ToolExecutionReconciliationErrorKind.Validation);
        if (!access.Groups.Overlaps(_options.ReconcilerGroups))
            throw Failure("TOOL_RECONCILER_ROLE_REQUIRED", "只有工具执行对账人员可以查看结果不确定记录。",
                ToolExecutionReconciliationErrorKind.Forbidden);
    }

    private Task TraceAsync(AccessContext access, string operation, string outcome,
        IReadOnlyDictionary<string, object?> details, CancellationToken cancellationToken)
    {
        var safeDetails = new Dictionary<string, object?>(details)
        {
            ["tenantId"] = access.TenantId,
            ["subjectId"] = access.SubjectId
        };
        return _traceSink.WriteAsync($"tool-reconciliation-{Guid.NewGuid():N}",
            [new TraceStep(operation, outcome, _timeProvider.GetUtcNow(), safeDetails)], cancellationToken);
    }

    private static bool FixedEquals(string left, string right) => CryptographicOperations.FixedTimeEquals(
        Encoding.ASCII.GetBytes(left), Encoding.ASCII.GetBytes(right));

    private string EncodeCursor(OutcomeUnknownToolExecutionPageKey key, string tenantId)
    {
        var payload = new CursorPayload(1, CursorSort, key.UpdatedAt.ToUniversalTime().UtcTicks,
            key.ExecutionKey);
        var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        var signature = SignCursor(payloadBytes, tenantId);
        return Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(new CursorEnvelope(
            payload.Version, payload.Sort, payload.UpdatedAtUtcTicks, payload.ExecutionKey,
            Base64UrlEncode(signature))));
    }

    private OutcomeUnknownToolExecutionPageKey DecodeCursor(string cursor, string tenantId)
    {
        var envelopeBytes = Base64UrlDecode(cursor, 2_048);
        var envelope = JsonSerializer.Deserialize<CursorEnvelope>(envelopeBytes) ?? throw new FormatException();
        // 只接受本服务生成的规范 JSON，拒绝重复字段、额外字段和非规范编码造成的歧义。
        if (!envelopeBytes.AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(envelope)))
            throw new FormatException();
        if (envelope.Version != 1 || !string.Equals(envelope.Sort, CursorSort, StringComparison.Ordinal)
            || string.IsNullOrEmpty(envelope.ExecutionKey) || envelope.ExecutionKey.Length != 64
            || envelope.ExecutionKey.Any(character => !Uri.IsHexDigit(character))
            || !string.Equals(envelope.ExecutionKey, envelope.ExecutionKey.ToUpperInvariant(),
                StringComparison.Ordinal))
            throw new FormatException();
        var payload = new CursorPayload(envelope.Version, envelope.Sort, envelope.UpdatedAtUtcTicks,
            envelope.ExecutionKey);
        var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        var suppliedSignature = Base64UrlDecode(envelope.Signature, 64);
        var expectedSignature = SignCursor(payloadBytes, tenantId);
        if (suppliedSignature.Length != expectedSignature.Length
            || !CryptographicOperations.FixedTimeEquals(suppliedSignature, expectedSignature))
            throw new FormatException();
        var updatedAt = new DateTimeOffset(envelope.UpdatedAtUtcTicks, TimeSpan.Zero);
        return new OutcomeUnknownToolExecutionPageKey(updatedAt, envelope.ExecutionKey);
    }

    private byte[] SignCursor(byte[] payload, string tenantId)
    {
        var context = Encoding.UTF8.GetBytes($"AiMentor.ToolExecutionCursor.v1\u001f{tenantId}\u001f");
        var authenticated = new byte[context.Length + payload.Length];
        context.CopyTo(authenticated, 0);
        payload.CopyTo(authenticated, context.Length);
        return HMACSHA256.HashData(_cursorSigningKey, authenticated);
    }

    private static string Base64UrlEncode(ReadOnlySpan<byte> value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string value, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength
            || value.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_'))
            || value.Length % 4 == 1)
            throw new FormatException();
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded += new string('=', (4 - padded.Length % 4) % 4);
        var decoded = Convert.FromBase64String(padded);
        if (!string.Equals(Base64UrlEncode(decoded), value, StringComparison.Ordinal))
            throw new FormatException();
        return decoded;
    }

    private sealed record CursorPayload(int Version, string Sort, long UpdatedAtUtcTicks, string ExecutionKey);
    private sealed record CursorEnvelope(
        int Version, string Sort, long UpdatedAtUtcTicks, string ExecutionKey, string Signature);

    private static ToolExecutionReconciliationException Failure(string code, string message,
        ToolExecutionReconciliationErrorKind kind) => new(code, message, kind);
}

/// <summary>只读检查原用户记忆标识和版本，判断删除副作用是否已经生效。</summary>
public sealed class MemoryDeleteOutcomeProbe(IMemoryStore memoryStore, TimeProvider timeProvider) : IToolOutcomeProbe
{
    public string ToolName => "memory.delete";

    /// <inheritdoc />
    public async Task<ToolOutcomeProbeResult> ProbeAsync(OutcomeUnknownToolExecution execution,
        JsonElement arguments, CancellationToken cancellationToken = default)
    {
        if (!arguments.TryGetProperty("memoryId", out var memoryIdElement)
            || memoryIdElement.ValueKind != JsonValueKind.String
            || !arguments.TryGetProperty("expectedVersion", out var versionElement)
            || !versionElement.TryGetInt32(out var expectedVersion) || expectedVersion <= 0)
            throw new ToolExecutionReconciliationException("MEMORY_DELETE_PROBE_ARGUMENTS_INVALID",
                "memory.delete 对账参数无效。", ToolExecutionReconciliationErrorKind.Validation);
        var memoryId = memoryIdElement.GetString()?.Trim();
        if (string.IsNullOrWhiteSpace(memoryId))
            throw new ToolExecutionReconciliationException("MEMORY_DELETE_PROBE_ARGUMENTS_INVALID",
                "memory.delete 对账参数无效。", ToolExecutionReconciliationErrorKind.Validation);

        var owner = AccessContext.Create(execution.TenantId, execution.SubjectId, []);
        var state = await memoryStore.ProbeTargetStateAsync(memoryId, owner, expectedVersion, cancellationToken);
        return state switch
        {
            MemoryTargetState.Absent => Result(execution.ExecutionKey, ToolOutcomeProbeState.Applied, "MEMORY_DELETE_APPLIED",
                "目标记忆已不存在，删除结果已在目标存储体现。"),
            MemoryTargetState.PresentAtExpectedVersion => Result(execution.ExecutionKey, ToolOutcomeProbeState.NotApplied,
                "MEMORY_DELETE_NOT_APPLIED", "目标记忆仍以原预期版本存在，删除未生效。"),
            MemoryTargetState.PresentAtDifferentVersion => Result(execution.ExecutionKey,
                ToolOutcomeProbeState.Indeterminate, "MEMORY_DELETE_TARGET_CHANGED",
                "目标记忆仍存在但版本已变化，不能把状态归因于原删除调用。"),
            _ => Result(execution.ExecutionKey, ToolOutcomeProbeState.Indeterminate,
                "MEMORY_DELETE_TARGET_INACCESSIBLE",
                "目标标识存在但不属于原调用者，不能据此认定删除已经生效。")
        };
    }

    private ToolOutcomeProbeResult Result(string executionKey, ToolOutcomeProbeState state, string code,
        string explanation) => new(executionKey, ToolName, state, code, explanation, timeProvider.GetUtcNow());
}
