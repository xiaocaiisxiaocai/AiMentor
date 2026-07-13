using System.Text.Json;
using AiMentor.Domain;

namespace AiMentor.Application;

public interface IServerTool
{
    ToolDescriptor Descriptor { get; }
    SafetyDecision ValidateArguments(JsonElement arguments);
    Task<JsonElement> ExecuteAsync(ToolExecutionContext context, JsonElement arguments,
        CancellationToken cancellationToken = default);
}

public sealed record ToolExecutionContext(AccessContext Access, string RunId);

public interface IToolRegistry
{
    IReadOnlyList<ToolDescriptor> Descriptors { get; }
    bool TryGet(string toolName, out IServerTool? tool);
}

public interface IToolExecutor
{
    Task<ToolExecutionResult> ExecuteAsync(string toolName, JsonElement arguments, AccessContext access,
        string? idempotencyKey = null, CancellationToken cancellationToken = default);
}

public sealed class ToolExecutorOptions
{
    public int MaximumArgumentBytes { get; init; } = 16 * 1024;
    public TimeSpan MaximumTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan IdempotencyRetention { get; init; } = TimeSpan.FromHours(1);
    public int MaximumIdempotencyEntries { get; init; } = 10_000;
}
