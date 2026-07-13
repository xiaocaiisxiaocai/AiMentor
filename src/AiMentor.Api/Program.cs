using System.Text.Json.Serialization;
using AiMentor.Api;
using AiMentor.Application;
using AiMentor.Domain;
using AiMentor.Infrastructure;
using Microsoft.Extensions.AI;

var builder = WebApplication.CreateBuilder(args);
builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

var knowledgeRoot = WorkspacePathLocator.FindKnowledgeRoot(
    builder.Configuration["Knowledge:RootPath"] ?? Environment.GetEnvironmentVariable("AIMENTOR_KNOWLEDGE_ROOT"));
builder.Services.AddSingleton<IKnowledgeRepository>(_ => new MarkdownKnowledgeRepository(knowledgeRoot));
builder.Services.AddSingleton<IInputSafetyService, RuleBasedInputSafetyService>();
builder.Services.AddSingleton<ITraceSink, InMemoryTraceSink>();
builder.Services.AddSingleton<IChatClient, DeterministicGroundedChatClient>();
builder.Services.AddSingleton<IAnswerComposer, AgentFrameworkAnswerComposer>();
builder.Services.AddSingleton(new TrustedQuestionOptions());
builder.Services.AddSingleton<ITrustedQuestionService, TrustedQuestionService>();

var app = builder.Build();
var repository = app.Services.GetRequiredService<IKnowledgeRepository>();
await repository.InitializeAsync();

app.MapGet("/health", () => Results.Ok(new { status = "healthy", knowledge = repository.Statistics }));
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
