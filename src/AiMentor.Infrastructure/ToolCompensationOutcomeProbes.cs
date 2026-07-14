using System.Text.Json;
using AiMentor.Application;
using AiMentor.Domain;

namespace AiMentor.Infrastructure;

/// <summary>在补偿服务信任专属探测器之前绑定记录身份并收窄可返回的数据边界。</summary>
internal static class ToolCompensationProbeResultValidator
{
    public static ToolCompensationOutcomeProbeResult Validate(
        ToolCompensationOutcomeProbeResult result,
        string expectedCompensationId,
        string expectedToolName,
        DateTimeOffset observedAt)
    {
        if (!string.Equals(result.CompensationId, expectedCompensationId, StringComparison.Ordinal)
            || !string.Equals(result.CompensationToolName, expectedToolName, StringComparison.Ordinal)
            || !Enum.IsDefined(result.State)
            || string.IsNullOrWhiteSpace(result.Code) || result.Code.Length > 128
            || result.Code.Any(character => !(character is >= 'A' and <= 'Z' or >= '0' and <= '9' or '_'))
            || string.IsNullOrWhiteSpace(result.Explanation) || result.Explanation.Length > 500)
            throw new ToolCompensationException("TOOL_COMPENSATION_PROBE_RESULT_INVALID",
                "反向工具探测器返回了与原补偿不匹配或超出边界的证据。",
                ToolCompensationErrorKind.Conflict);

        // 证据时间由编排服务在接收结果时盖章，不能由可替换探测器延长复核窗口。
        return result with { ObservedAt = observedAt };
    }
}

/// <summary>
/// 核验 memory.correct.restore 是否把目标恢复为加密快照中的旧值和期限。
/// 仅接受原所有者身份读取目标，并且不会把旧值或当前值写入结果、API 或 Trace。
/// </summary>
public sealed class MemoryCorrectRestoreOutcomeProbe(
    IMemoryStore memoryStore,
    TimeProvider timeProvider) : IToolCompensationOutcomeProbe
{
    public string CompensationToolName => "memory.correct.restore";

    /// <inheritdoc />
    public async Task<ToolCompensationOutcomeProbeResult> ProbeAsync(string compensationId, AccessContext owner,
        JsonElement compensationState, CancellationToken cancellationToken = default)
    {
        var snapshot = compensationState.Deserialize<MemoryCorrectionSnapshot>()
            ?? throw InvalidSnapshot();
        if (string.IsNullOrWhiteSpace(snapshot.MemoryId) || snapshot.ExpectedVersionAfterForward <= 1)
            throw InvalidSnapshot();

        var state = await memoryStore.ProbeCorrectionCompensationAsync(snapshot.MemoryId, owner,
            snapshot.ExpectedVersionAfterForward, snapshot.PreviousValue, snapshot.PreviousExpiresAt,
            cancellationToken);
        return state switch
        {
            MemoryCorrectionCompensationState.Applied => Result(compensationId, ToolOutcomeProbeState.Applied,
                "MEMORY_CORRECT_RESTORE_APPLIED",
                "目标已进入反向恢复生成的下一版本，且受保护状态与补偿快照一致。"),
            MemoryCorrectionCompensationState.NotApplied => Result(compensationId, ToolOutcomeProbeState.NotApplied,
                "MEMORY_CORRECT_RESTORE_NOT_APPLIED",
                "目标仍处于正向更正产生的版本，反向恢复尚未生效。"),
            MemoryCorrectionCompensationState.Changed => Result(compensationId,
                ToolOutcomeProbeState.Indeterminate, "MEMORY_CORRECT_RESTORE_TARGET_CHANGED",
                "目标版本或受保护状态已发生其他变化，不能归因于本次反向恢复。"),
            _ => Result(compensationId, ToolOutcomeProbeState.Indeterminate,
                "MEMORY_CORRECT_RESTORE_TARGET_INACCESSIBLE",
                "目标记忆不存在或不属于原所有者，不能认定反向恢复结果。")
        };
    }

    private ToolCompensationOutcomeProbeResult Result(string compensationId, ToolOutcomeProbeState state,
        string code, string explanation) => new(compensationId, CompensationToolName, state, code, explanation,
        timeProvider.GetUtcNow());

    private static ToolCompensationException InvalidSnapshot() => new(
        "MEMORY_CORRECT_RESTORE_SNAPSHOT_INVALID", "memory.correct.restore 补偿快照无效。",
        ToolCompensationErrorKind.Conflict);

    private sealed record MemoryCorrectionSnapshot(
        string MemoryId,
        string PreviousValue,
        int ExpectedVersionAfterForward,
        DateTimeOffset PreviousExpiresAt);
}
