using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using AiMentor.Infrastructure;
using Microsoft.Extensions.AI;

var builder = WebApplication.CreateBuilder(args);
// Fixture 不记录请求、Header 或异常正文，避免协议验收自身扩大敏感信息传播面。
builder.Logging.ClearProviders();
var app = builder.Build();

var apiKey = Environment.GetEnvironmentVariable("AIMENTOR_PROVIDER_FIXTURE_API_KEY");
if (string.IsNullOrWhiteSpace(apiKey))
    throw new InvalidOperationException("PROVIDER_FIXTURE_API_KEY_MISSING");
var permanentError = string.Equals(Environment.GetEnvironmentVariable("AIMENTOR_PROVIDER_FIXTURE_MODE"),
    "PermanentError", StringComparison.OrdinalIgnoreCase);
var rateLimitOnce = string.Equals(Environment.GetEnvironmentVariable("AIMENTOR_PROVIDER_FIXTURE_429_ONCE"),
    "true", StringComparison.OrdinalIgnoreCase);
var requests = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);
var rateLimited = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);
var safety = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);
using var deterministicChat = new DeterministicGroundedChatClient();

app.MapGet("/health", () => Results.Json(new { status = "Ready" }));
app.MapGet("/__fixture/state", (HttpRequest request) => Authorized(request)
    ? Results.Json(new
    {
        requests = Snapshot(requests),
        rateLimited = Snapshot(rateLimited),
        sensitivePayloads = safety.GetValueOrDefault("sensitivePayloads"),
        mode = permanentError ? "PermanentError" : "Success"
    })
    : Results.Unauthorized());

app.MapPost("/v1/chat/completions", async (HttpRequest request, ChatCompletionRequest payload,
    CancellationToken cancellationToken) =>
{
    Inspect(payload.Messages.Select(message => message.Content));
    var early = BeforeRequest(request, "chat");
    if (early is not null) return early;
    var messages = payload.Messages.Select(message => new ChatMessage(Role(message.Role), message.Content)).ToArray();
    var response = await deterministicChat.GetResponseAsync(messages, cancellationToken: cancellationToken);
    return Results.Json(new
    {
        id = "fixture-chat-completion",
        @object = "chat.completion",
        created = 0,
        model = payload.Model,
        choices = new[]
        {
            new
            {
                index = 0,
                message = new { role = "assistant", content = response.Text },
                finish_reason = "stop"
            }
        },
        usage = new { prompt_tokens = 0, completion_tokens = 0, total_tokens = 0 }
    });
});

app.MapPost("/v1/embeddings", (HttpRequest request, EmbeddingRequest payload) =>
{
    Inspect([payload.Input]);
    var early = BeforeRequest(request, "embeddings");
    if (early is not null) return early;
    if (payload.Dimensions is < 1 or > 4096)
        return Results.BadRequest(new { error = new { code = "invalid_dimensions" } });
    var vector = new float[payload.Dimensions];
    vector[0] = 1;
    return Results.Json(new
    {
        @object = "list",
        model = payload.Model,
        data = new[] { new { @object = "embedding", index = 0, embedding = vector } },
        usage = new { prompt_tokens = 0, total_tokens = 0 }
    });
});

app.MapPost("/rerank", (HttpRequest request, RerankRequest payload) =>
{
    Inspect(payload.Documents.Prepend(payload.Query));
    var early = BeforeRequest(request, "reranker");
    if (early is not null) return early;
    if (payload.Documents.Count == 0)
        return Results.BadRequest(new { error = new { code = "documents_required" } });
    // 递减分数保持检索层已经确定的顺序；该 Fixture 只证明协议适配，不声称具备语义排序质量。
    var scores = payload.Documents.Select((_, index) =>
        Math.Round((payload.Documents.Count - index) / (double)payload.Documents.Count, 6)).ToArray();
    return Results.Json(new { model = payload.Model, scores });
});

await app.RunAsync();

IResult? BeforeRequest(HttpRequest request, string component)
{
    if (!Authorized(request)) return Results.Unauthorized();
    var count = requests.AddOrUpdate(component, 1, static (_, current) => current + 1);
    if (rateLimitOnce && count == 1)
    {
        rateLimited.AddOrUpdate(component, 1, static (_, current) => current + 1);
        request.HttpContext.Response.Headers.RetryAfter = "0";
        return Results.Json(new { error = new { code = "rate_limit_exceeded" } }, statusCode: 429);
    }
    return permanentError
        ? Results.Json(new { error = new { code = "fixture_provider_unavailable" } }, statusCode: 503)
        : null;
}

void Inspect(IEnumerable<string> values)
{
    string[] forbidden =
    [
        "alice.fixture@example.test", "13800138000", "sk_live_1234567890abcdef", "11010519491231002X",
        "INDIRECT-INJECTION-CANARY-20260714"
    ];
    // 只累计违规次数，不保留命中的输入或证据正文。
    if (values.Any(value => forbidden.Any(marker => value.Contains(marker, StringComparison.Ordinal))))
        safety.AddOrUpdate("sensitivePayloads", 1, static (_, current) => current + 1);
}

bool Authorized(HttpRequest request)
{
    const string prefix = "Bearer ";
    var authorization = request.Headers.Authorization.ToString();
    if (!authorization.StartsWith(prefix, StringComparison.Ordinal)) return false;
    var supplied = Encoding.UTF8.GetBytes(authorization[prefix.Length..]);
    var expected = Encoding.UTF8.GetBytes(apiKey);
    return supplied.Length == expected.Length && CryptographicOperations.FixedTimeEquals(supplied, expected);
}

static IReadOnlyDictionary<string, int> Snapshot(ConcurrentDictionary<string, int> source) =>
    source.OrderBy(item => item.Key, StringComparer.Ordinal)
        .ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);

static ChatRole Role(string value) => value.ToLowerInvariant() switch
{
    "assistant" => ChatRole.Assistant,
    "system" => ChatRole.System,
    "tool" => ChatRole.Tool,
    _ => ChatRole.User
};

internal sealed record ChatCompletionRequest(
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("messages")] IReadOnlyList<FixtureChatMessage> Messages);

internal sealed record FixtureChatMessage(
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("content")] string Content);

internal sealed record EmbeddingRequest(
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("input")] string Input,
    [property: JsonPropertyName("dimensions")] int Dimensions);

internal sealed record RerankRequest(
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("query")] string Query,
    [property: JsonPropertyName("documents")] IReadOnlyList<string> Documents);
