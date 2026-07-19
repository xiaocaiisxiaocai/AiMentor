using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using AiMentor.Application;
using AiMentor.Infrastructure;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace AiMentor.Tests;

public sealed class OperationsHttpContractTests
{
    [Fact]
    public async Task RealProgramRequiresAuthenticationAndSupportsRequestReviewAndLocationRead()
    {
        using var factory = new OperationsApiFactory();
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.GetAsync("/api/v1/operations/tasks?type=approval")).StatusCode);

        var approvalResponse = await SendAsync(client, HttpMethod.Post, "/api/v1/tool-approvals",
            "requester", ["users"], new
            {
                toolName = "memory.delete",
                arguments = new { memoryId = "http-contract-memory", expectedVersion = 1 },
                justification = "真实 HTTP 运营动作契约"
            });
        Assert.Equal(HttpStatusCode.Accepted, approvalResponse.StatusCode);
        var approvalId = (await JsonDocument.ParseAsync(await approvalResponse.Content.ReadAsStreamAsync()))
            .RootElement.GetProperty("id").GetString()!;

        var tasksResponse = await SendAsync(client, HttpMethod.Get,
            "/api/v1/operations/tasks?type=approval", "operator-a", ["tool-approvers"]);
        Assert.Equal(HttpStatusCode.OK, tasksResponse.StatusCode);
        using var tasks = await JsonDocument.ParseAsync(await tasksResponse.Content.ReadAsStreamAsync());
        var task = tasks.RootElement.GetProperty("items").EnumerateArray()
            .Single(item => item.GetProperty("id").GetString() == approvalId);
        var targetETag = task.GetProperty("eTag").GetString()!;

        var forbiddenRequest = await SendAsync(client, HttpMethod.Post,
            $"/api/v1/operations/tasks/approval/{approvalId}/actions", "reader", ["users"],
            new { action = "approve", reason = "无权提出" }, targetETag, "http-forbidden-001");
        Assert.Equal(HttpStatusCode.Forbidden, forbiddenRequest.StatusCode);

        var requestResponse = await SendAsync(client, HttpMethod.Post,
            $"/api/v1/operations/tasks/approval/{approvalId}/actions", "operator-a", ["tool-approvers"],
            new { action = "approve", reason = "提出批准" }, targetETag, "http-request-0001");
        Assert.Equal(HttpStatusCode.Accepted, requestResponse.StatusCode);
        Assert.NotNull(requestResponse.Headers.Location);
        var location = requestResponse.Headers.Location!.OriginalString;
        using var requested = await JsonDocument.ParseAsync(await requestResponse.Content.ReadAsStreamAsync());
        var actionId = requested.RootElement.GetProperty("id").GetString()!;
        var actionETag = requested.RootElement.GetProperty("eTag").GetString()!;
        var version = requested.RootElement.GetProperty("version").GetInt64();
        Assert.Equal($"/api/v1/operations/actions/{actionId}", location);

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(location)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await SendAsync(client, HttpMethod.Get, location, "reader", ["users"])).StatusCode);
        var pending = await SendAsync(client, HttpMethod.Get, location, "operator-a", ["tool-approvers"]);
        Assert.Equal(HttpStatusCode.OK, pending.StatusCode);

        var staleReview = await SendAsync(client, HttpMethod.Post, $"{location}/review", "operator-b",
            ["tool-approvers"], new { expectedVersion = version, approved = true, reason = "独立复核" },
            "\"stale-etag\"");
        Assert.Equal(HttpStatusCode.Conflict, staleReview.StatusCode);

        var reviewResponse = await SendAsync(client, HttpMethod.Post, $"{location}/review", "operator-b",
            ["tool-approvers"], new { expectedVersion = version, approved = true, reason = "独立复核" },
            actionETag);
        Assert.Equal(HttpStatusCode.OK, reviewResponse.StatusCode);
        using var reviewed = await JsonDocument.ParseAsync(await reviewResponse.Content.ReadAsStreamAsync());
        Assert.Equal("Completed", reviewed.RootElement.GetProperty("status").GetString());

        var completed = await SendAsync(client, HttpMethod.Get, location, "operator-b", ["tool-approvers"]);
        Assert.Equal(HttpStatusCode.OK, completed.StatusCode);
        using var completedBody = await JsonDocument.ParseAsync(await completed.Content.ReadAsStreamAsync());
        Assert.Equal("Completed", completedBody.RootElement.GetProperty("status").GetString());
        Assert.Equal("OPERATIONS_APPROVAL_APPROVED",
            completedBody.RootElement.GetProperty("outcomeCode").GetString());

        var ledger = factory.Services.GetRequiredService<IToolExecutionLedger>();
        for (var index = 0; index < 51; index++)
        {
            var executionKey = index.ToString("X64", System.Globalization.CultureInfo.InvariantCulture);
            var acquired = await ledger.TryAcquireAsync(new ToolExecutionLedgerRequest(
                    executionKey, new string('A', 64), $"http-run-{index}", "tenant-http", "owner-a",
                    "memory.delete"),
                TimeSpan.FromMinutes(1), TimeSpan.FromHours(1), 100);
            await ledger.MarkExecutingAsync(executionKey, acquired.LeaseToken!);
            await ledger.MarkOutcomeUnknownAsync(executionKey, acquired.LeaseToken!);
        }
        var legacyPage = await SendAsync(client, HttpMethod.Get,
            "/api/v1/tool-executions/outcome-unknown?limit=50", "reconciler-a", ["tool-reconcilers"]);
        Assert.Equal(HttpStatusCode.OK, legacyPage.StatusCode);
        using var legacyBody = await JsonDocument.ParseAsync(await legacyPage.Content.ReadAsStreamAsync());
        Assert.Equal(JsonValueKind.Array, legacyBody.RootElement.ValueKind);
        Assert.Equal(50, legacyBody.RootElement.GetArrayLength());
        var nextCursor = Assert.Single(legacyPage.Headers.GetValues("X-AiMentor-Next-Cursor"));

        var cursorPage = await SendAsync(client, HttpMethod.Get,
            $"/api/v1/tool-executions/outcome-unknown?limit=50&cursor={Uri.EscapeDataString(nextCursor)}",
            "reconciler-a", ["tool-reconcilers"]);
        Assert.Equal(HttpStatusCode.OK, cursorPage.StatusCode);
        using var cursorBody = await JsonDocument.ParseAsync(await cursorPage.Content.ReadAsStreamAsync());
        Assert.Equal(1, cursorBody.RootElement.GetProperty("items").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, cursorBody.RootElement.GetProperty("nextCursor").ValueKind);

        var tamperedCursor = nextCursor[..^1] + (nextCursor[^1] == 'A' ? 'B' : 'A');
        var tamperedPage = await SendAsync(client, HttpMethod.Get,
            $"/api/v1/tool-executions/outcome-unknown?limit=50&cursor={Uri.EscapeDataString(tamperedCursor)}",
            "reconciler-a", ["tool-reconcilers"]);
        Assert.Equal(HttpStatusCode.BadRequest, tamperedPage.StatusCode);
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string path,
        string subject, string[] groups, object? body = null, string? ifMatch = null,
        string? idempotencyKey = null)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-Test-Subject", subject);
        request.Headers.Add("X-Test-Groups", string.Join(',', groups));
        if (ifMatch is not null) request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        if (idempotencyKey is not null) request.Headers.Add("Idempotency-Key", idempotencyKey);
        if (body is not null) request.Content = JsonContent.Create(body);
        return await client.SendAsync(request);
    }

    private sealed class OperationsApiFactory : WebApplicationFactory<Program>
    {
        private readonly string _storePath = Path.Combine(Path.GetTempPath(), $"aimentor-http-{Guid.NewGuid():N}.json");

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development")
                .UseSetting("Authentication:Mode", "OidcJwt")
                .UseSetting("Authentication:Authority", "https://identity.test")
                .UseSetting("Authentication:Audience", "aimentor-http-tests")
                .UseSetting("Workflow:Provider", "InMemory")
                .UseSetting("Memory:StorePath", _storePath)
                .UseSetting("Memory:EncryptionKey", Convert.ToBase64String(new byte[32]));
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Authentication:Mode"] = "OidcJwt",
                    ["Authentication:Authority"] = "https://identity.test",
                    ["Authentication:Audience"] = "aimentor-http-tests",
                    ["Workflow:Provider"] = "InMemory",
                    ["Memory:StorePath"] = _storePath,
                    ["Memory:EncryptionKey"] = Convert.ToBase64String(new byte[32])
                }));
            builder.ConfigureTestServices(services => services.AddAuthentication(options =>
                {
                    options.DefaultAuthenticateScheme = HeaderAuthenticationHandler.SchemeName;
                    options.DefaultChallengeScheme = HeaderAuthenticationHandler.SchemeName;
                })
                .AddScheme<AuthenticationSchemeOptions, HeaderAuthenticationHandler>(
                    HeaderAuthenticationHandler.SchemeName, _ => { }));
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (File.Exists(_storePath)) File.Delete(_storePath);
        }
    }

    private sealed class HeaderAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string SchemeName = "OperationsHttpTest";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var subject = Request.Headers["X-Test-Subject"].ToString();
            if (string.IsNullOrWhiteSpace(subject))
                return Task.FromResult(AuthenticateResult.NoResult());
            var claims = new List<Claim>
            {
                new("sub", subject),
                new("tenant_id", "tenant-http")
            };
            claims.AddRange(Request.Headers["X-Test-Groups"].ToString()
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(group => new Claim("groups", group)));
            var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName)));
        }
    }
}
