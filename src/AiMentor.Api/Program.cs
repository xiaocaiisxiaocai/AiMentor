using System.Text.Json.Serialization;
using System.Net.Http.Headers;
using System.Text;
using AiMentor.Api;
using AiMentor.Application;
using AiMentor.Domain;
using AiMentor.Infrastructure;
using Microsoft.Extensions.AI;

var builder = WebApplication.CreateBuilder(args);
builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

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
var repository = app.Services.GetRequiredService<IKnowledgeRepository>();
await repository.InitializeAsync();

app.MapGet("/health", () => Results.Ok(new { status = "healthy", ragProvider, knowledge = repository.Statistics }));
app.MapGet("/api/knowledge/stats", () => Results.Ok(repository.Statistics));

app.MapPost("/api/questions", async (AskRequest request, ITrustedQuestionService service, CancellationToken cancellationToken) =>
{
    var answer = await service.AskAsync(new TrustedQuestion(
        request.Question,
        AccessContext.Create(request.TenantId, request.SubjectId, request.Groups)), cancellationToken);
    return answer.Decision switch
    {
        AnswerDecision.Refused => Results.Json(answer, statusCode: StatusCodes.Status403Forbidden),
        AnswerDecision.Failed => Results.Json(answer, statusCode: StatusCodes.Status503ServiceUnavailable),
        _ => Results.Ok(answer)
    };
});

app.MapPost("/api/questions/stream", async (AskRequest request, ITrustedQuestionService service, HttpResponse response, CancellationToken cancellationToken) =>
{
    response.ContentType = "text/event-stream";
    await response.WriteAsync("event: run.started\ndata: {}\n\n", cancellationToken);
    await response.Body.FlushAsync(cancellationToken);
    var answer = await service.AskAsync(new TrustedQuestion(request.Question,
        AccessContext.Create(request.TenantId, request.SubjectId, request.Groups)), cancellationToken);
    await response.WriteAsync($"event: answer.completed\ndata: {System.Text.Json.JsonSerializer.Serialize(answer)}\n\n", cancellationToken);
});

await app.RunAsync();
