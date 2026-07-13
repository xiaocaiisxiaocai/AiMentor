using System.Text.Json;
using System.Text.RegularExpressions;
using AiMentor.Application;
using AiMentor.Domain;
using AiMentor.Infrastructure;
using Microsoft.Extensions.AI;

var evaluationFile = WorkspacePathLocator.FindEvaluationFile(args.FirstOrDefault());
var knowledgeRoot = WorkspacePathLocator.FindKnowledgeRoot(args.Skip(1).FirstOrDefault());
using var repository = new MarkdownKnowledgeRepository(knowledgeRoot);
await repository.InitializeAsync();
using IChatClient chatClient = new DeterministicGroundedChatClient();
var service = new TrustedQuestionService(repository, new RuleBasedInputSafetyService(),
    new AgentFrameworkAnswerComposer(chatClient), new InMemoryTraceSink(), new TrustedQuestionOptions());

var inputJsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
var cases = (await File.ReadAllLinesAsync(evaluationFile))
    .Where(line => !string.IsNullOrWhiteSpace(line))
    .Select(line => JsonSerializer.Deserialize<EvaluationCase>(line, inputJsonOptions) ?? throw new InvalidDataException("测评数据包含无效 JSON。"))
    .ToArray();

var results = new List<EvaluationResult>(cases.Length);
var allGroups = new[] { "all-employees", "all-rnd", "data-platform", "middleware-group", "knowledge-admin", "security" };
foreach (var item in cases)
{
    var answer = await service.AskAsync(new TrustedQuestion(item.Input,
        AccessContext.Create(item.TenantId, $"evaluation:{item.CaseId}", allGroups), item.CaseId));
    var expectedDecisionMatched = MatchDecision(item.ExpectedAction, answer.Decision);
    var expectedSources = Regex.Matches(item.Evidence, @"BK-[A-Z]+-\d{3}").Select(match => match.Value).Distinct(StringComparer.Ordinal).ToArray();
    var citationMatched = expectedSources.Length == 0 || expectedSources.All(expected =>
        answer.Citations.Any(citation => string.Equals(citation.DocumentId, expected, StringComparison.Ordinal)));
    results.Add(new EvaluationResult(item.CaseId, item.Category, item.ExpectedAction, answer.Decision.ToString(),
        expectedDecisionMatched, citationMatched, answer.Citations.Select(x => x.DocumentId).ToArray()));
}

var decisionAccuracy = results.Count(x => x.DecisionMatched) / (double)results.Count;
var sourceCases = results.Where((_, index) => Regex.IsMatch(cases[index].Evidence, @"BK-[A-Z]+-\d{3}")).ToArray();
var citationRecall = sourceCases.Length == 0 ? 1 : sourceCases.Count(x => x.CitationMatched) / (double)sourceCases.Length;
Console.WriteLine(JsonSerializer.Serialize(new
{
    generatedAt = DateTimeOffset.UtcNow,
    total = results.Count,
    knowledge = repository.Statistics,
    decisionAccuracy,
    citationRecall,
    byCategory = results.GroupBy(x => x.Category).Select(group => new
    {
        category = group.Key,
        total = group.Count(),
        decisionAccuracy = group.Count(x => x.DecisionMatched) / (double)group.Count(),
        citationRecall = group.Count(x => x.CitationMatched) / (double)group.Count()
    }),
    failures = results.Where(x => !x.DecisionMatched || !x.CitationMatched).Take(30)
}, new JsonSerializerOptions { WriteIndented = true }));

static bool MatchDecision(string expectedAction, AnswerDecision actual) => expectedAction switch
{
    "answer" => actual == AnswerDecision.Answered,
    "clarify_or_refuse" => actual is AnswerDecision.Refused or AnswerDecision.InsufficientEvidence,
    "memory_policy" => actual is AnswerDecision.Refused or AnswerDecision.InsufficientEvidence,
    _ => actual is AnswerDecision.Answered or AnswerDecision.Refused or AnswerDecision.InsufficientEvidence
};

internal sealed record EvaluationCase(
    [property: System.Text.Json.Serialization.JsonPropertyName("case_id")] string CaseId,
    string Category,
    [property: System.Text.Json.Serialization.JsonPropertyName("tenant_id")] string TenantId,
    string Input,
    string Evidence,
    [property: System.Text.Json.Serialization.JsonPropertyName("expected_action")] string ExpectedAction);

internal sealed record EvaluationResult(string CaseId, string Category, string ExpectedAction, string ActualDecision,
    bool DecisionMatched, bool CitationMatched, IReadOnlyList<string> Citations);
