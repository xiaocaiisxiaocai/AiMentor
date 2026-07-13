using System.ComponentModel.DataAnnotations;

namespace AiMentor.Api;

public sealed record AskV1Request(
    [property: Required, StringLength(4_000, MinimumLength = 1)] string Question);

public sealed record LegacyAskRequest(
    [property: Required, StringLength(4_000, MinimumLength = 1)] string Question,
    [property: Required, StringLength(128, MinimumLength = 1)] string TenantId,
    [property: Required, StringLength(128, MinimumLength = 1)] string SubjectId,
    [property: Required, MinLength(1), MaxLength(64)] IReadOnlyList<string> Groups);
