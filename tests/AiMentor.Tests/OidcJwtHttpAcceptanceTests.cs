using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using AiMentor.Api;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace AiMentor.Tests;

public sealed class OidcJwtHttpAcceptanceTests
{
    private const string Issuer = "https://controlled-idp.example";
    private const string Audience = "aimentor-api";

    [Fact]
    public async Task JwtHttpPipelineEnforcesIdentityRotationAndBoundedRevocation()
    {
        using var keyA = RSA.Create(2048);
        using var keyB = RSA.Create(2048);
        var signingA = Key(keyA, "kid-a");
        var signingB = Key(keyB, "kid-b");
        var configuration = new MutableOidcConfiguration(Issuer, signingA);
        using var server = CreateServer(configuration);
        using var client = server.CreateClient();

        var userA = Token(signingA, "user-a", groups: ["operators"]);
        var userB = Token(signingA, "user-b", groups: ["readers"]);
        Assert.Equal(HttpStatusCode.OK, (await GetAsync(client, "/runs/user-a", userA)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await GetAsync(client, "/runs/user-a", userB)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await GetAsync(client, "/runs/user-a", Token(signingA, "user-a", issuer: "https://wrong"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await GetAsync(client, "/runs/user-a", Token(signingA, "user-a", audience: "wrong"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await GetAsync(client, "/runs/user-a", Token(signingA, "user-a", expires: DateTime.UtcNow.AddMinutes(-2)))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await GetAsync(client, "/runs/user-a", Token(signingA, "user-a", duplicateSubject: "user-b"))).StatusCode);

        using var unknownRsa = RSA.Create(2048);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await GetAsync(client, "/runs/user-a", Token(Key(unknownRsa, "kid-unknown"), "user-a"))).StatusCode);
        Assert.True(configuration.RefreshRequests > 0);

        configuration.SetKeys(signingA, signingB);
        Assert.Equal(HttpStatusCode.OK,
            (await GetAsync(client, "/runs/user-a", Token(signingB, "user-a"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await GetAsync(client, "/runs/user-a", userA)).StatusCode);
        configuration.SetKeys(signingB);
        Assert.Equal(HttpStatusCode.Unauthorized, (await GetAsync(client, "/runs/user-a", userA)).StatusCode);

        var oldPrivilegedToken = Token(signingB, "user-a", groups: ["operators"]);
        var newRevokedToken = Token(signingB, "user-a", groups: ["readers"]);
        Assert.Equal(HttpStatusCode.OK, (await GetAsync(client, "/operators", oldPrivilegedToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await GetAsync(client, "/operators", newRevokedToken)).StatusCode);
        // 自包含旧 Token 在其 TTL+ClockSkew 窗口内仍有效；即时撤权只对身份方签发的新 Token 作承诺。
        Assert.Equal(HttpStatusCode.OK, (await GetAsync(client, "/operators", oldPrivilegedToken)).StatusCode);
    }

    private static TestServer CreateServer(IConfigurationManager<OpenIdConnectConfiguration> configuration)
    {
        var options = new AiMentorAuthenticationOptions
        {
            Mode = "OidcJwt", Authority = Issuer, Audience = Audience,
            SubjectClaim = "sub", TenantClaim = "tenant_id", GroupsClaim = "groups"
        };
#pragma warning disable ASPDEPR004, ASPDEPR008 // 旧宿主仅用于进程内受控认证测试。
        var builder = new WebHostBuilder().ConfigureServices(services =>
        {
            services.AddRouting();
            services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(jwt =>
            {
                jwt.ConfigurationManager = configuration;
                jwt.MapInboundClaims = false;
                jwt.IncludeErrorDetails = false;
                jwt.RefreshOnIssuerKeyNotFound = true;
                jwt.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true, ValidIssuer = Issuer,
                    ValidateAudience = true, ValidAudience = Audience,
                    ValidateLifetime = true, ValidateIssuerSigningKey = true,
                    ClockSkew = TimeSpan.FromMinutes(1)
                };
                jwt.Events = new JwtBearerEvents
                {
                    OnTokenValidated = context =>
                    {
                        try { _ = ClaimsAccessContextProvider.CreateValidated(context.Principal!, options); }
                        catch (UnauthorizedAccessException) { context.Fail("JWT 身份声明无效或存在歧义。"); }
                        return Task.CompletedTask;
                    }
                };
            });
            services.AddAuthorization();
        }).Configure(app =>
        {
            app.UseRouting();
            app.UseAuthentication();
            app.UseAuthorization();
            app.UseEndpoints(endpoints =>
            {
                endpoints.MapGet("/runs/{owner}", (string owner, ClaimsPrincipal principal) =>
                {
                    var access = ClaimsAccessContextProvider.CreateValidated(principal, options);
                    return access.SubjectId == owner ? Results.Ok() : Results.Forbid();
                }).RequireAuthorization();
                endpoints.MapGet("/operators", (ClaimsPrincipal principal) =>
                {
                    var access = ClaimsAccessContextProvider.CreateValidated(principal, options);
                    return access.Groups.Contains("operators") ? Results.Ok() : Results.Forbid();
                }).RequireAuthorization();
            });
        });
        var server = new TestServer(builder);
#pragma warning restore ASPDEPR004, ASPDEPR008
        return server;
    }

    private static RsaSecurityKey Key(RSA rsa, string kid) => new(rsa) { KeyId = kid };

    private static string Token(SecurityKey key, string subject, string? issuer = null, string? audience = null,
        DateTime? expires = null, string[]? groups = null, string? duplicateSubject = null)
    {
        var claims = new List<Claim>
        {
            new("sub", subject), new("tenant_id", "tenant-a")
        };
        if (duplicateSubject is not null) claims.Add(new Claim("sub", duplicateSubject));
        claims.AddRange((groups ?? ["readers"]).Select(group => new Claim("groups", group)));
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer ?? Issuer, Audience = audience ?? Audience,
            Subject = new ClaimsIdentity(claims), NotBefore = DateTime.UtcNow.AddMinutes(-1),
            Expires = expires ?? DateTime.UtcNow.AddMinutes(5),
            SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.RsaSha256)
        });
    }

    private static Task<HttpResponseMessage> GetAsync(HttpClient client, string path, string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client.SendAsync(request);
    }

    private sealed class MutableOidcConfiguration : IConfigurationManager<OpenIdConnectConfiguration>
    {
        private readonly string _issuer;
        private SecurityKey[] _keys;
        private int _refreshRequests;

        public MutableOidcConfiguration(string issuer, params SecurityKey[] keys) { _issuer = issuer; _keys = keys; }
        public int RefreshRequests => Volatile.Read(ref _refreshRequests);
        public void SetKeys(params SecurityKey[] keys) => Volatile.Write(ref _keys, keys);
        public Task<OpenIdConnectConfiguration> GetConfigurationAsync(CancellationToken cancel)
        {
            var result = new OpenIdConnectConfiguration { Issuer = _issuer };
            foreach (var key in Volatile.Read(ref _keys)) result.SigningKeys.Add(key);
            return Task.FromResult(result);
        }
        public void RequestRefresh() => Interlocked.Increment(ref _refreshRequests);
    }
}
