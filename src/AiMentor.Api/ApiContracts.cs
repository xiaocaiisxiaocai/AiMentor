using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using AiMentor.Domain;

namespace AiMentor.Api;

public sealed record AskV1Request(
    [property: Required, StringLength(4_000, MinimumLength = 1)] string Question);

public sealed record LegacyAskRequest(
    [property: Required, StringLength(4_000, MinimumLength = 1)] string Question,
    [property: Required, StringLength(128, MinimumLength = 1)] string TenantId,
    [property: Required, StringLength(128, MinimumLength = 1)] string SubjectId,
    [property: Required, MinLength(1), MaxLength(64)] IReadOnlyList<string> Groups);

public sealed record ProposeMemoryRequest(
    MemoryScope Scope,
    [property: Required, StringLength(128, MinimumLength = 1)] string Key,
    [property: Required, StringLength(1_000, MinimumLength = 1)] string Value,
    [property: StringLength(128, MinimumLength = 1)] string? SessionId = null,
    DateTimeOffset? ExpiresAt = null);

public sealed record CorrectMemoryRequest(
    [property: Required, StringLength(1_000, MinimumLength = 1)] string Value,
    [property: Range(1, int.MaxValue)] int ExpectedVersion,
    DateTimeOffset? ExpiresAt = null);

public sealed record ExecuteToolRequest(Dictionary<string, JsonElement>? Arguments = null);
