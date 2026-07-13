using System.Text.Json;

namespace AiMentor.Domain;

public enum ToolOperationRisk { ReadOnly, Mutation, Privileged }
public enum ToolExecutionStatus { Completed, Rejected, RequiresApproval, TimedOut, ResultTooLarge, Failed }

public sealed record ToolDescriptor(
    string Name,
    string Description,
    ToolOperationRisk Risk,
    TimeSpan Timeout,
    int MaximumResultBytes,
    bool RequiresIdempotencyKey);

public sealed record ToolExecutionResult(
    string RunId,
    string ToolName,
    ToolExecutionStatus Status,
    JsonElement? Output,
    SafetyDecision Safety,
    bool IdempotentReplay,
    IReadOnlyList<TraceStep> Trace);
