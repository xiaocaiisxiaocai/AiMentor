using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);
// Fixture 不记录请求和令牌，避免验收工具自身扩大 Access Token 传播面。
builder.Logging.ClearProviders();
var app = builder.Build();

var issuer = RequiredEnvironment("AIMENTOR_OIDC_FIXTURE_ISSUER").TrimEnd('/');
var audience = RequiredEnvironment("AIMENTOR_OIDC_FIXTURE_AUDIENCE");
var adminKey = RequiredEnvironment("AIMENTOR_OIDC_FIXTURE_ADMIN_KEY");
using var keys = new SigningKeyRing();
string[] responseTypes = ["token"];
string[] subjectTypes = ["public"];
string[] signingAlgorithms = [SecurityAlgorithms.RsaSha256];
var discoveryRequests = 0;
var jwksRequests = 0;

app.MapGet("/health", () => Results.Json(new { status = "Ready" }));
app.MapGet("/.well-known/openid-configuration", () =>
{
    Interlocked.Increment(ref discoveryRequests);
    return Results.Json(new
    {
        issuer,
        jwks_uri = $"{issuer}/jwks",
        token_endpoint = $"{issuer}/__fixture/tokens",
        response_types_supported = responseTypes,
        subject_types_supported = subjectTypes,
        id_token_signing_alg_values_supported = signingAlgorithms
    });
});
app.MapGet("/jwks", () =>
{
    Interlocked.Increment(ref jwksRequests);
    return Results.Json(new { keys = keys.PublishedKeys().Select(PublicJwk).ToArray() });
});
app.MapPost("/__fixture/tokens", (HttpRequest request, TokenRequest input) =>
{
    if (!Authorized(request)) return Results.Unauthorized();
    if (string.IsNullOrWhiteSpace(input.Subject) || string.IsNullOrWhiteSpace(input.TenantId))
        return Results.BadRequest(new { code = "OIDC_FIXTURE_IDENTITY_REQUIRED" });
    var signingKey = string.Equals(input.Key, "unknown", StringComparison.OrdinalIgnoreCase)
        ? keys.UnknownKey
        : keys.CurrentKey;
    var claims = new List<Claim>
    {
        new("sub", input.Subject.Trim()),
        new("tenant_id", input.TenantId.Trim())
    };
    claims.AddRange((input.Groups ?? []).Where(group => !string.IsNullOrWhiteSpace(group))
        .Select(group => new Claim("groups", group.Trim())));
    if (!string.IsNullOrWhiteSpace(input.DuplicateSubject))
        claims.Add(new Claim("sub", input.DuplicateSubject.Trim()));
    var now = DateTime.UtcNow;
    var token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
    {
        Issuer = input.Issuer ?? issuer,
        Audience = input.Audience ?? audience,
        Subject = new ClaimsIdentity(claims),
        NotBefore = now.AddMinutes(-10),
        Expires = now.AddSeconds(input.ExpiresInSeconds ?? 300),
        SigningCredentials = new SigningCredentials(signingKey, SecurityAlgorithms.RsaSha256)
    });
    return Results.Json(new { access_token = token, token_type = "Bearer" });
});
app.MapPost("/__fixture/rotate", (HttpRequest request) =>
{
    if (!Authorized(request)) return Results.Unauthorized();
    keys.RotateAndReplacePublishedKey();
    return Results.Json(new { status = "Rotated", kid = keys.CurrentKey.KeyId });
});
app.MapGet("/__fixture/state", (HttpRequest request) => Authorized(request)
    ? Results.Json(new
    {
        discoveryRequests = Volatile.Read(ref discoveryRequests),
        jwksRequests = Volatile.Read(ref jwksRequests),
        currentKid = keys.CurrentKey.KeyId
    })
    : Results.Unauthorized());

await app.RunAsync();

bool Authorized(HttpRequest request)
{
    const string prefix = "Bearer ";
    var authorization = request.Headers.Authorization.ToString();
    if (!authorization.StartsWith(prefix, StringComparison.Ordinal)) return false;
    var supplied = Encoding.UTF8.GetBytes(authorization[prefix.Length..]);
    var expected = Encoding.UTF8.GetBytes(adminKey);
    return supplied.Length == expected.Length && CryptographicOperations.FixedTimeEquals(supplied, expected);
}

static object PublicJwk(RsaSecurityKey key)
{
    var parameters = key.Rsa?.ExportParameters(false) ?? key.Parameters;
    return new
    {
        kty = "RSA",
        use = "sig",
        kid = key.KeyId,
        alg = SecurityAlgorithms.RsaSha256,
        n = Base64UrlEncoder.Encode(parameters.Modulus),
        e = Base64UrlEncoder.Encode(parameters.Exponent)
    };
}

static string RequiredEnvironment(string name) =>
    Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
        ? value
        : throw new InvalidOperationException($"{name}_MISSING");

internal sealed record TokenRequest(string Subject, string TenantId, string[]? Groups = null,
    string? Issuer = null, string? Audience = null, int? ExpiresInSeconds = null,
    string? DuplicateSubject = null, string? Key = null);

internal sealed class SigningKeyRing : IDisposable
{
    private readonly RSA _rsaA = RSA.Create(2048);
    private readonly RSA _rsaB = RSA.Create(2048);
    private readonly RSA _unknownRsa = RSA.Create(2048);
    private RsaSecurityKey _current;
    private RsaSecurityKey[] _published;

    public SigningKeyRing()
    {
        KeyA = new RsaSecurityKey(_rsaA) { KeyId = "fixture-kid-a" };
        KeyB = new RsaSecurityKey(_rsaB) { KeyId = "fixture-kid-b" };
        UnknownKey = new RsaSecurityKey(_unknownRsa) { KeyId = "fixture-kid-unknown" };
        _current = KeyA;
        _published = [KeyA];
    }

    private RsaSecurityKey KeyA { get; }
    private RsaSecurityKey KeyB { get; }
    public RsaSecurityKey UnknownKey { get; }
    public RsaSecurityKey CurrentKey => Volatile.Read(ref _current);
    public RsaSecurityKey[] PublishedKeys() => Volatile.Read(ref _published);

    public void RotateAndReplacePublishedKey()
    {
        Volatile.Write(ref _published, [KeyB]);
        Volatile.Write(ref _current, KeyB);
    }

    public void Dispose()
    {
        _rsaA.Dispose();
        _rsaB.Dispose();
        _unknownRsa.Dispose();
    }
}
