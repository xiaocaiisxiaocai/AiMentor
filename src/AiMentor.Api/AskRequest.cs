namespace AiMentor.Api;

public sealed record AskRequest(string Question, string TenantId, string SubjectId, IReadOnlyList<string>? Groups);
