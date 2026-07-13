using System.Security.Claims;
using AiMentor.Domain;

namespace AiMentor.Api;

public sealed class AiMentorAuthenticationOptions
{
    public string Mode { get; init; } = "Development";
    public string? Authority { get; init; }
    public string? Audience { get; init; }
    public string SubjectClaim { get; init; } = "sub";
    public string TenantClaim { get; init; } = "tenant_id";
    public string GroupsClaim { get; init; } = "groups";
    public string DevelopmentTenantId { get; init; } = "demo-beichen";
    public string DevelopmentSubjectId { get; init; } = "development-user";
    public string[] DevelopmentGroups { get; init; } = ["all-rnd"];
}

public interface IRequestAccessContextProvider
{
    AccessContext GetAccessContext(ClaimsPrincipal principal);
}

public sealed class DevelopmentAccessContextProvider(AiMentorAuthenticationOptions options) : IRequestAccessContextProvider
{
    public AccessContext GetAccessContext(ClaimsPrincipal principal) => AccessContext.Create(
        options.DevelopmentTenantId,
        options.DevelopmentSubjectId,
        options.DevelopmentGroups);
}

public sealed class ClaimsAccessContextProvider(AiMentorAuthenticationOptions options) : IRequestAccessContextProvider
{
    public AccessContext GetAccessContext(ClaimsPrincipal principal)
    {
        if (principal.Identity?.IsAuthenticated != true) throw new UnauthorizedAccessException("调用方尚未通过 JWT 身份验证。");
        var subject = RequiredClaim(principal, options.SubjectClaim);
        var tenant = RequiredClaim(principal, options.TenantClaim);
        var groups = principal.FindAll(options.GroupsClaim)
            .Select(claim => claim.Value.Trim())
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return AccessContext.Create(tenant, subject, groups);
    }

    private static string RequiredClaim(ClaimsPrincipal principal, string claimType)
    {
        var values = principal.FindAll(claimType).Select(claim => claim.Value.Trim()).Where(value => value.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
        return values.Length == 1
            ? values[0]
            : throw new UnauthorizedAccessException($"JWT 必须且只能包含一个 {claimType} 声明。");
    }
}
