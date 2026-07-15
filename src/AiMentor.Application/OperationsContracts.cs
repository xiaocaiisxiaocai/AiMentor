using AiMentor.Domain;

namespace AiMentor.Application;

public sealed record OperationsTaskSummary(string Id, string Type, string Status, DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt, DateTimeOffset? DueAt, bool Overdue, IReadOnlyList<string> AllowedActions, string ETag);

public sealed record OperationsTaskPage(IReadOnlyList<OperationsTaskSummary> Items, string? NextCursor);

public interface IOperationsTaskService
{
    Task<OperationsTaskPage> ListAsync(AccessContext access, string? type, string? status, string? cursor,
        int limit, CancellationToken cancellationToken = default);
}
