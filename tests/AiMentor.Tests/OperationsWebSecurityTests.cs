using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using AiMentor.Application;
using AiMentor.Domain;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace AiMentor.Tests;

public sealed class OperationsWebSecurityTests
{
    [Fact]
    public async Task EnabledBrowserBffFailsClosedWithoutServerSession()
    {
        using var factory = new AuthenticatedOperationsWebFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true
        });

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/ops/session")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/ops/api/tasks")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/ops/index.html")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/ops/unlisted-key.xml")).StatusCode);

        var claims = new[]
        {
            new Claim("sub", "operator-cookie"),
            new Claim("tenant_id", "tenant-cookie"),
            new Claim("groups", "tool-approvers")
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "AiMentor.Operations.Cookie"));
        var cookieOptions = factory.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get("AiMentor.Operations.Cookie");
        var protectedTicket = cookieOptions.TicketDataFormat.Protect(
            new AuthenticationTicket(principal, "AiMentor.Operations.Cookie"));
        client.DefaultRequestHeaders.Add("Cookie", $"AiMentor.Operations={Uri.EscapeDataString(protectedTicket)}");

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/ops/session")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/ops/api/tasks")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.GetAsync("/api/v1/operations/tasks")).StatusCode);
    }

    [Fact]
    public async Task BrowserBffRequiresCsrfAndNeverDependsOnBrowserAccessToken()
    {
        using var factory = new OperationsWebFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true
        });

        var index = await client.GetAsync("/ops/index.html");
        Assert.Equal(HttpStatusCode.OK, index.StatusCode);
        Assert.Contains("default-src 'self'", Header(index, "Content-Security-Policy"),
            StringComparison.Ordinal);
        Assert.Equal("nosniff", Header(index, "X-Content-Type-Options"));
        Assert.Equal("no-referrer", Header(index, "Referrer-Policy"));

        var script = await client.GetStringAsync("/ops/app.js");
        Assert.Contains("/ops/session", script, StringComparison.Ordinal);
        Assert.Contains("/ops/api/tasks", script, StringComparison.Ordinal);
        Assert.DoesNotContain("localStorage", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("access_token", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/api/v1/operations", script, StringComparison.Ordinal);

        var approvalService = factory.Services.GetRequiredService<IToolApprovalService>();
        var approval = await approvalService.RequestAsync("memory.delete",
            JsonSerializer.SerializeToElement(new { memoryId = "ops-web-memory", expectedVersion = 1 }),
            "运营台 BFF 防伪验收", AccessContext.Create("tenant-ops-web", "requester-ops-web", ["users"]));
        var approvalId = approval.Id;

        var tasksResponse = await client.GetAsync("/ops/api/tasks?type=approval");
        Assert.Equal(HttpStatusCode.OK, tasksResponse.StatusCode);
        Assert.Equal("no-store", Header(tasksResponse, "Cache-Control"));
        using var tasks = await JsonDocument.ParseAsync(await tasksResponse.Content.ReadAsStreamAsync());
        var task = tasks.RootElement.GetProperty("items").EnumerateArray()
            .Single(item => item.GetProperty("id").GetString() == approvalId);
        var etag = task.GetProperty("eTag").GetString()!;

        var actionPath = $"/ops/api/tasks/approval/{approvalId}/actions";
        using var missingCsrf = Request(actionPath, etag, includeCsrf: null);
        var missingCsrfResponse = await client.SendAsync(missingCsrf);
        Assert.Equal(HttpStatusCode.BadRequest, missingCsrfResponse.StatusCode);
        using var csrfProblem = await JsonDocument.ParseAsync(
            await missingCsrfResponse.Content.ReadAsStreamAsync());
        Assert.Equal("OPERATIONS_CSRF_INVALID",
            csrfProblem.RootElement.GetProperty("code").GetString());

        var sessionResponse = await client.GetAsync("/ops/session");
        Assert.Equal(HttpStatusCode.OK, sessionResponse.StatusCode);
        Assert.Equal("no-store", Header(sessionResponse, "Cache-Control"));
        using var session = await JsonDocument.ParseAsync(await sessionResponse.Content.ReadAsStreamAsync());
        Assert.True(session.RootElement.GetProperty("authenticated").GetBoolean());
        Assert.False(session.RootElement.GetProperty("authenticationRequired").GetBoolean());
        var csrfToken = session.RootElement.GetProperty("csrfToken").GetString();
        Assert.False(string.IsNullOrWhiteSpace(csrfToken));

        using var validCsrf = Request(actionPath, etag, csrfToken);
        var validCsrfResponse = await client.SendAsync(validCsrf);
        Assert.Equal(HttpStatusCode.Accepted, validCsrfResponse.StatusCode);
        Assert.NotNull(validCsrfResponse.Headers.Location);
        Assert.StartsWith("/ops/api/actions/", validCsrfResponse.Headers.Location!.OriginalString,
            StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK,
            (await client.GetAsync(validCsrfResponse.Headers.Location)).StatusCode);
    }

    private static HttpRequestMessage Request(string path, string etag, string? includeCsrf)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(new { action = "approve", reason = "提出批准并等待第二人复核" })
        };
        request.Headers.TryAddWithoutValidation("If-Match", etag);
        request.Headers.Add("Idempotency-Key", $"ops-web-{Guid.NewGuid():N}");
        if (includeCsrf is not null) request.Headers.Add("X-AiMentor-CSRF", includeCsrf);
        return request;
    }

    private static string Header(HttpResponseMessage response, string name) =>
        Assert.Single(response.Headers.GetValues(name));

    private sealed class OperationsWebFactory : WebApplicationFactory<Program>
    {
        private readonly string _storePath = Path.Combine(Path.GetTempPath(), $"aimentor-ops-web-{Guid.NewGuid():N}.json");

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development")
                .UseSetting("Authentication:Mode", "Development")
                .UseSetting("Authentication:Development:TenantId", "tenant-ops-web")
                .UseSetting("Authentication:Development:SubjectId", "operator-ops-web")
                .UseSetting("Authentication:Development:Groups:0", "tool-approvers")
                .UseSetting("Workflow:Provider", "InMemory")
                .UseSetting("Memory:StorePath", _storePath)
                .UseSetting("Memory:EncryptionKey", Convert.ToBase64String(new byte[32]));
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Authentication:Mode"] = "Development",
                    ["Authentication:Development:TenantId"] = "tenant-ops-web",
                    ["Authentication:Development:SubjectId"] = "operator-ops-web",
                    ["Authentication:Development:Groups:0"] = "tool-approvers",
                    ["Workflow:Provider"] = "InMemory",
                    ["Memory:StorePath"] = _storePath,
                    ["Memory:EncryptionKey"] = Convert.ToBase64String(new byte[32])
                }));
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (File.Exists(_storePath)) File.Delete(_storePath);
        }
    }

    private sealed class AuthenticatedOperationsWebFactory : WebApplicationFactory<Program>
    {
        private readonly string _storePath = Path.Combine(Path.GetTempPath(),
            $"aimentor-ops-auth-{Guid.NewGuid():N}.json");

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development")
                .UseSetting("Authentication:Mode", "OidcJwt")
                .UseSetting("Authentication:Authority", "https://identity.test")
                .UseSetting("Authentication:Audience", "aimentor-operations-api")
                .UseSetting("Operations:Web:Enabled", "true")
                .UseSetting("Operations:Web:ClientId", "aimentor-operations-web")
                .UseSetting("Operations:Web:ClientSecret", Convert.ToBase64String(new byte[32]))
                .UseSetting("Operations:Web:Scopes:0", "openid")
                .UseSetting("Operations:Web:Scopes:1", "profile")
                .UseSetting("Workflow:Provider", "InMemory")
                .UseSetting("Memory:StorePath", _storePath)
                .UseSetting("Memory:EncryptionKey", Convert.ToBase64String(new byte[32]));
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (File.Exists(_storePath)) File.Delete(_storePath);
        }
    }
}
