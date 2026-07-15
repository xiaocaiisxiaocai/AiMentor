using System.Security.Claims;
using System.Text.Json;
using AiMentor.Domain;

namespace AiMentor.Api;

/// <summary>配置开发身份或 OIDC JWT 声明映射。</summary>
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

/// <summary>从已验证主体构造应用层访问上下文。</summary>
public interface IRequestAccessContextProvider
{
    AccessContext GetAccessContext(ClaimsPrincipal principal);
}

/// <summary>仅在开发模式使用服务端固定身份，避免请求体自报权限。</summary>
public sealed class DevelopmentAccessContextProvider(AiMentorAuthenticationOptions options) : IRequestAccessContextProvider
{
    public AccessContext GetAccessContext(ClaimsPrincipal principal) => AccessContext.Create(
        options.DevelopmentTenantId,
        options.DevelopmentSubjectId,
        options.DevelopmentGroups);
}

/// <summary>从通过签名和受众验证的 JWT claims 构造访问身份。</summary>
public sealed class ClaimsAccessContextProvider(AiMentorAuthenticationOptions options) : IRequestAccessContextProvider
{
    public AccessContext GetAccessContext(ClaimsPrincipal principal) => CreateValidated(principal, options);

    /// <summary>同时供 JwtBearer 成功事件使用，使歧义身份在进入端点前稳定失败为 401。</summary>
    public static AccessContext CreateValidated(ClaimsPrincipal principal, AiMentorAuthenticationOptions options)
    {
        if (principal.Identity?.IsAuthenticated != true) throw new UnauthorizedAccessException("调用方尚未通过 JWT 身份验证。");
        var subject = RequiredClaim(principal, options.SubjectClaim);
        var tenant = RequiredClaim(principal, options.TenantClaim);
        var groups = principal.FindAll(options.GroupsClaim)
            .SelectMany(ParseGroups)
            .Select(value => value.Trim())
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return AccessContext.Create(tenant, subject, groups);
    }

    private static IEnumerable<string> ParseGroups(Claim claim)
    {
        var value = claim.Value.Trim();
        if (value.Length == 0 || value[0] != '[') return [value];
        try
        {
            using var document = JsonDocument.Parse(value);
            if (document.RootElement.ValueKind != JsonValueKind.Array
                || document.RootElement.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.String))
                throw new UnauthorizedAccessException("JWT groups 数组必须只包含字符串。");
            return document.RootElement.EnumerateArray().Select(item => item.GetString()!).ToArray();
        }
        catch (JsonException)
        {
            throw new UnauthorizedAccessException("JWT groups 声明不是有效的字符串数组。");
        }
    }

    private static string RequiredClaim(ClaimsPrincipal principal, string claimType)
    {
        var values = principal.FindAll(claimType).Select(claim => claim.Value.Trim()).Where(value => value.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
        return values.Length == 1
            ? values[0]
            : throw new UnauthorizedAccessException($"JWT 必须且只能包含一个 {claimType} 声明。");
    }
}
