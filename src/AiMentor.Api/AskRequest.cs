using System.ComponentModel.DataAnnotations;

namespace AiMentor.Api;

public sealed record AskRequest(
    [property: Required, StringLength(4_000, MinimumLength = 1)] string Question,
    [property: Required, StringLength(128, MinimumLength = 1)] string TenantId,
    [property: Required, StringLength(128, MinimumLength = 1)] string SubjectId,
    [property: Required, MinLength(1), MaxLength(64)] IReadOnlyList<string> Groups);
