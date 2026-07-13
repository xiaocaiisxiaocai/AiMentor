using System.Text.Json.Serialization;
using System.Text.Json;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.RateLimiting;
using AiMentor.Api;
using AiMentor.Application;
using AiMentor.Domain;
using AiMentor.Infrastructure;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using HttpJsonOptions = Microsoft.AspNetCore.Http.Json.JsonOptions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Validation;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);
var questionRateLimit = builder.Configuration.GetValue("Api:QuestionRateLimitPerMinute", 60);
if (questionRateLimit <= 0) throw new InvalidOperationException("Api:QuestionRateLimitPerMinute 必须大于 0。");
var enableLegacyV0 = builder.Configuration.GetValue("Api:EnableLegacyV0", false);
var authenticationOptions = new AiMentorAuthenticationOptions
{
    Mode = builder.Configuration["Authentication:Mode"] ?? "Development",
    Authority = builder.Configuration["Authentication:Authority"],
    Audience = builder.Configuration["Authentication:Audience"],
    SubjectClaim = builder.Configuration["Authentication:SubjectClaim"] ?? "sub",
    TenantClaim = builder.Configuration["Authentication:TenantClaim"] ?? "tenant_id",
    GroupsClaim = builder.Configuration["Authentication:GroupsClaim"] ?? "groups",
    DevelopmentTenantId = builder.Configuration["Authentication:Development:TenantId"] ?? "demo-beichen",
    DevelopmentSubjectId = builder.Configuration["Authentication:Development:SubjectId"] ?? "development-user",
    DevelopmentGroups = builder.Configuration.GetSection("Authentication:Development:Groups").Get<string[]>() ?? ["all-rnd"]
};
var jwtAuthenticationEnabled = string.Equals(authenticationOptions.Mode, "OidcJwt", StringComparison.OrdinalIgnoreCase);
if (builder.Environment.IsProduction() && (!jwtAuthenticationEnabled || enableLegacyV0))
    throw new InvalidOperationException("Production 环境必须启用 Authentication:Mode=OidcJwt 且禁用 Api:EnableLegacyV0。");
if (jwtAuthenticationEnabled)
{
    if (!Uri.TryCreate(authenticationOptions.Authority, UriKind.Absolute, out var authorityUri) || authorityUri.Scheme != Uri.UriSchemeHttps)
        throw new InvalidOperationException("OIDC JWT 模式要求 Authentication:Authority 为有效的 HTTPS 地址。");
    if (string.IsNullOrWhiteSpace(authenticationOptions.Audience))
        throw new InvalidOperationException("OIDC JWT 模式要求配置 Authentication:Audience。");
    builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(jwtOptions =>
    {
        jwtOptions.Authority = authenticationOptions.Authority;
        jwtOptions.Audience = authenticationOptions.Audience;
        jwtOptions.RequireHttpsMetadata = true;
        jwtOptions.MapInboundClaims = false;
        jwtOptions.IncludeErrorDetails = false;
        jwtOptions.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            NameClaimType = authenticationOptions.SubjectClaim,
            ClockSkew = TimeSpan.FromMinutes(1)
        };
    });
    builder.Services.AddAuthorization(authorizationOptions => authorizationOptions.AddPolicy("question-api", policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.RequireClaim(authenticationOptions.SubjectClaim);
        policy.RequireClaim(authenticationOptions.TenantClaim);
    }));
    builder.Services.AddSingleton<IRequestAccessContextProvider>(new ClaimsAccessContextProvider(authenticationOptions));
}
else
{
    builder.Services.AddSingleton<IRequestAccessContextProvider>(new DevelopmentAccessContextProvider(authenticationOptions));
}
builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi(openApiOptions =>
{
    if (jwtAuthenticationEnabled)
    {
        openApiOptions.AddDocumentTransformer<BearerSecuritySchemeTransformer>();
        openApiOptions.AddOperationTransformer<BearerSecuritySchemeTransformer>();
    }
});
builder.Services.AddValidation();
builder.Services.AddRateLimiter(rateLimitOptions =>
{
    rateLimitOptions.AddPolicy("questions", context =>
    {
        var partitionKey = context.User.FindFirst("sub")?.Value
            ?? context.Connection.RemoteIpAddress?.ToString()
            ?? "unknown";
        return RateLimitPartition.GetFixedWindowLimiter(partitionKey, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = questionRateLimit,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            AutoReplenishment = true
        });
    });
    rateLimitOptions.OnRejected = async (context, cancellationToken) =>
    {
        context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        context.HttpContext.Response.ContentType = "application/problem+json";
        var problem = new ProblemDetails
        {
            Status = StatusCodes.Status429TooManyRequests,
            Title = "请求过于频繁",
            Detail = $"问答 API 每个调用方每分钟最多允许 {questionRateLimit} 次请求。",
            Type = "https://httpstatuses.com/429",
            Instance = context.HttpContext.Request.Path
        };
        await context.HttpContext.Response.WriteAsJsonAsync(problem, cancellationToken);
    };
});

var knowledgeRoot = WorkspacePathLocator.FindKnowledgeRoot(
    builder.Configuration["Knowledge:RootPath"] ?? Environment.GetEnvironmentVariable("AIMENTOR_KNOWLEDGE_ROOT"));
builder.Services.AddSingleton(new MarkdownKnowledgeRepository(knowledgeRoot));
builder.Services.AddSingleton<IKnowledgeChunkSource>(services => services.GetRequiredService<MarkdownKnowledgeRepository>());
builder.Services.AddSingleton<ITextEmbeddingGenerator, DeterministicEmbeddingGenerator>();

var ragProvider = builder.Configuration["Rag:Provider"] ?? "Local";
if (string.Equals(ragProvider, "OpenSearch", StringComparison.OrdinalIgnoreCase))
{
    var endpoint = builder.Configuration["OpenSearch:Endpoint"] ?? "http://127.0.0.1:9200";
    var client = new HttpClient { BaseAddress = new Uri(endpoint.TrimEnd('/') + "/"), Timeout = TimeSpan.FromSeconds(30) };
    var username = builder.Configuration["OpenSearch:Username"] ?? Environment.GetEnvironmentVariable("AIMENTOR_OPENSEARCH_USERNAME");
    var password = builder.Configuration["OpenSearch:Password"] ?? Environment.GetEnvironmentVariable("AIMENTOR_OPENSEARCH_PASSWORD");
    if (!string.IsNullOrWhiteSpace(username) && password is not null)
    {
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}")));
    }
    builder.Services.AddSingleton(client);
    builder.Services.AddSingleton(new OpenSearchOptions
    {
        IndexName = builder.Configuration["OpenSearch:IndexName"] ?? "aimentor-knowledge-v1",
        SearchPipelineName = builder.Configuration["OpenSearch:SearchPipelineName"] ?? "aimentor-hybrid-v1",
        SynchronizeOnStartup = builder.Configuration.GetValue("OpenSearch:SynchronizeOnStartup", true)
    });
    builder.Services.AddSingleton<IKnowledgeRepository, OpenSearchKnowledgeRepository>();
}
else
{
    builder.Services.AddSingleton<IKnowledgeRepository>(services => services.GetRequiredService<MarkdownKnowledgeRepository>());
}
builder.Services.AddSingleton<IInputSafetyService, RuleBasedInputSafetyService>();
builder.Services.AddSingleton<ITraceSink, InMemoryTraceSink>();
builder.Services.AddSingleton<IChatClient, DeterministicGroundedChatClient>();
builder.Services.AddSingleton<IAnswerComposer, AgentFrameworkAnswerComposer>();
builder.Services.AddSingleton(new TrustedQuestionOptions());
builder.Services.AddSingleton<ITrustedQuestionService, TrustedQuestionService>();

var app = builder.Build();
app.UseExceptionHandler();
if (jwtAuthenticationEnabled)
{
    app.UseAuthentication();
    app.UseAuthorization();
}
app.UseRateLimiter();
app.MapOpenApi();
var repository = app.Services.GetRequiredService<IKnowledgeRepository>();
await repository.InitializeAsync();

app.MapGet("/health", () => Results.Ok(new { status = "healthy", ragProvider, authenticationMode = authenticationOptions.Mode, knowledge = repository.Statistics }))
    .WithName("Health").WithTags("System").DisableRateLimiting();

var v1 = app.MapGroup("/api/v1").WithTags("AiMentor v1");
if (jwtAuthenticationEnabled) v1.RequireAuthorization("question-api");
v1.MapGet("/knowledge/stats", () => Results.Ok(repository.Statistics))
    .WithName("GetKnowledgeStatisticsV1").Produces<KnowledgeStatistics>();
v1.MapPost("/questions", AskAsync)
    .WithName("AskQuestionV1")
    .WithSummary("提交可信问答请求")
    .Produces<TrustedAnswer>()
    .ProducesValidationProblem()
    .ProducesProblem(StatusCodes.Status401Unauthorized)
    .ProducesProblem(StatusCodes.Status403Forbidden)
    .ProducesProblem(StatusCodes.Status429TooManyRequests)
    .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
    .RequireRateLimiting("questions");
v1.MapPost("/questions/stream", StreamAsync)
    .WithName("StreamQuestionV1")
    .WithSummary("通过 Server-Sent Events 返回可信问答事件")
    .Produces(StatusCodes.Status200OK, contentType: "text/event-stream")
    .ProducesValidationProblem()
    .ProducesProblem(StatusCodes.Status401Unauthorized)
    .ProducesProblem(StatusCodes.Status403Forbidden)
    .ProducesProblem(StatusCodes.Status429TooManyRequests)
    .RequireRateLimiting("questions");

// V0 仅用于短期迁移，默认关闭，Production 环境禁止启用。
if (enableLegacyV0)
{
    app.MapGet("/api/knowledge/stats", () => Results.Ok(repository.Statistics)).ExcludeFromDescription();
    app.MapPost("/api/questions", LegacyAskAsync).RequireRateLimiting("questions").ExcludeFromDescription();
}

await app.RunAsync();

static async Task<IResult> AskAsync(AskV1Request request, ITrustedQuestionService service, IRequestAccessContextProvider accessProvider,
    HttpContext context,
    CancellationToken cancellationToken)
{
    var correlationId = GetCorrelationId(context);
    var access = accessProvider.GetAccessContext(context.User);
    var answer = await service.AskAsync(new TrustedQuestion(request.Question,
        access, correlationId), cancellationToken);
    context.Response.Headers["X-Run-ID"] = answer.RunId;
    if (answer.Decision != AnswerDecision.Failed) return Results.Ok(answer);
    return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "回答生成失败",
        detail: answer.Answer, extensions: new Dictionary<string, object?> { ["runId"] = answer.RunId });
}

static async Task StreamAsync(AskV1Request request, ITrustedQuestionService service, IRequestAccessContextProvider accessProvider, HttpContext context,
    IOptions<HttpJsonOptions> jsonOptions, CancellationToken cancellationToken)
{
    var runId = GetCorrelationId(context);
    context.Response.ContentType = "text/event-stream";
    context.Response.Headers.CacheControl = "no-cache";
    context.Response.Headers["X-Accel-Buffering"] = "no";
    context.Response.Headers["X-Run-ID"] = runId;
    await WriteEventAsync(context.Response, "run.started", new { runId }, jsonOptions.Value.SerializerOptions, cancellationToken);
    var access = accessProvider.GetAccessContext(context.User);
    var answer = await service.AskAsync(new TrustedQuestion(request.Question,
        access, runId), cancellationToken);
    await WriteEventAsync(context.Response, "answer.completed", answer, jsonOptions.Value.SerializerOptions, cancellationToken);
}

static async Task<IResult> LegacyAskAsync(LegacyAskRequest request, ITrustedQuestionService service, HttpContext context,
    CancellationToken cancellationToken)
{
    var runId = GetCorrelationId(context);
    var answer = await service.AskAsync(new TrustedQuestion(request.Question,
        AccessContext.Create(request.TenantId, request.SubjectId, request.Groups), runId), cancellationToken);
    context.Response.Headers["X-Run-ID"] = answer.RunId;
    return Results.Ok(answer);
}

static async Task WriteEventAsync(HttpResponse response, string eventName, object payload, JsonSerializerOptions options,
    CancellationToken cancellationToken)
{
    await response.WriteAsync($"event: {eventName}\ndata: {JsonSerializer.Serialize(payload, options)}\n\n", cancellationToken);
    await response.Body.FlushAsync(cancellationToken);
}

static string GetCorrelationId(HttpContext context)
{
    var requested = context.Request.Headers["X-Correlation-ID"].ToString().Trim();
    if (requested.Length is > 0 and <= 128 && requested.All(character => char.IsLetterOrDigit(character) || character is '-' or '_' or '.'))
        return requested;
    return Guid.NewGuid().ToString("N");
}
