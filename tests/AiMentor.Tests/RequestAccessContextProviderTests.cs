using System.Security.Claims;
using AiMentor.Api;
using Xunit;

namespace AiMentor.Tests;

public sealed class RequestAccessContextProviderTests
{
    private static readonly AiMentorAuthenticationOptions Options = new()
    {
        SubjectClaim = "sub",
        TenantClaim = "tenant_id",
        GroupsClaim = "groups"
    };

    [Fact]
    public void ClaimsProviderShouldBuildAccessContextOnlyFromValidatedPrincipal()
    {
        var identity = new ClaimsIdentity(
        [
            new Claim("sub", "user-001"),
            new Claim("tenant_id", "demo-beichen"),
            new Claim("groups", "all-rnd"),
            new Claim("groups", "data-platform")
        ], "Bearer");

        var access = new ClaimsAccessContextProvider(Options).GetAccessContext(new ClaimsPrincipal(identity));

        Assert.Equal("user-001", access.SubjectId);
        Assert.Equal("demo-beichen", access.TenantId);
        Assert.Equal(2, access.Groups.Count);
        Assert.Contains("data-platform", access.Groups);
    }

    [Fact]
    public void ClaimsProviderShouldRejectUnauthenticatedPrincipal()
    {
        var provider = new ClaimsAccessContextProvider(Options);

        Assert.Throws<UnauthorizedAccessException>(() => provider.GetAccessContext(new ClaimsPrincipal(new ClaimsIdentity())));
    }

    [Theory]
    [InlineData("sub")]
    [InlineData("tenant_id")]
    public void ClaimsProviderShouldRejectMissingRequiredClaim(string omittedClaim)
    {
        var claims = new List<Claim>();
        if (omittedClaim != "sub") claims.Add(new Claim("sub", "user-001"));
        if (omittedClaim != "tenant_id") claims.Add(new Claim("tenant_id", "demo-beichen"));
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer"));

        Assert.Throws<UnauthorizedAccessException>(() => new ClaimsAccessContextProvider(Options).GetAccessContext(principal));
    }

    [Fact]
    public void DevelopmentProviderShouldIgnoreCallerSuppliedClaims()
    {
        var options = new AiMentorAuthenticationOptions
        {
            DevelopmentTenantId = "demo-beichen",
            DevelopmentSubjectId = "fixed-developer",
            DevelopmentGroups = ["all-rnd"]
        };
        var malicious = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("tenant_id", "attacker"), new Claim("groups", "security")], "Fake"));

        var access = new DevelopmentAccessContextProvider(options).GetAccessContext(malicious);

        Assert.Equal("demo-beichen", access.TenantId);
        Assert.Equal("fixed-developer", access.SubjectId);
        Assert.DoesNotContain("security", access.Groups);
    }
}
