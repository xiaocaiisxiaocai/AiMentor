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
var service = new TrustedQuestionService(repository, new RuleBasedQueryNormalizer(), new RuleBasedInputSafetyService(),
    new RuleBasedRetrievedContentSafetyService(),
    new LexicalEvidenceReranker(), new RuleBasedEvidenceSufficiencyEvaluator(),
    new AgentFrameworkAnswerComposer(chatClient), new RuleBasedOutputSafetyService(),
    new InMemoryTraceSink(), new TrustedQuestionOptions());

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
    var terminalCode = answer.Trace.Reverse()
        .Select(step => step.Details.TryGetValue("code", out var code) ? code as string : null)
        .FirstOrDefault(code => !string.IsNullOrWhiteSpace(code)) ?? answer.Safety.Code;
    results.Add(new EvaluationResult(item.CaseId, item.Category, item.ExpectedAction, answer.Decision.ToString(),
        answer.Safety.Code, terminalCode, expectedDecisionMatched, citationMatched, answer.Citations.Select(x => x.DocumentId).ToArray()));
}

var decisionAccuracy = results.Count(x => x.DecisionMatched) / (double)results.Count;
var sourceCases = results.Where((_, index) => Regex.IsMatch(cases[index].Evidence, @"BK-[A-Z]+-\d{3}")).ToArray();
var citationRecall = sourceCases.Length == 0 ? 1 : sourceCases.Count(x => x.CitationMatched) / (double)sourceCases.Length;
var categoryMetrics = results.GroupBy(x => x.Category).Select(group => new CategoryMetric(
    group.Key,
    group.Count(),
    group.Count(x => x.DecisionMatched) / (double)group.Count(),
    group.Count(x => x.CitationMatched) / (double)group.Count())).ToArray();
const double minimumDecisionAccuracy = 0.89;
const double minimumCitationRecall = 0.71;
var requiredPerfectCategories = new[] { "no_answer", "memory", "security", "acl" };
var gatePassed = decisionAccuracy >= minimumDecisionAccuracy
    && citationRecall >= minimumCitationRecall
    && requiredPerfectCategories.All(category => categoryMetrics.Any(metric =>
        metric.Category == category && metric.DecisionAccuracy == 1));
Console.WriteLine(JsonSerializer.Serialize(new
{
    generatedAt = DateTimeOffset.UtcNow,
    total = results.Count,
    knowledge = repository.Statistics,
    decisionAccuracy,
    citationRecall,
    qualityGate = new { passed = gatePassed, minimumDecisionAccuracy, minimumCitationRecall, requiredPerfectCategories },
    byCategory = categoryMetrics,
    failures = results.Where(x => !x.DecisionMatched || !x.CitationMatched)
}, new JsonSerializerOptions { WriteIndented = true }));
if (!gatePassed) Environment.ExitCode = 2;

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
    string SafetyCode, string TerminalCode, bool DecisionMatched, bool CitationMatched, IReadOnlyList<string> Citations);

internal sealed record CategoryMetric(string Category, int Total, double DecisionAccuracy, double CitationRecall);
