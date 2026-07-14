using System.Text.Json;
using System.Text.Json.Serialization;
using AiMentor.Application;
using AiMentor.Evaluation;
using AiMentor.Infrastructure;
using Microsoft.Extensions.AI;

const int qualityFailureExitCode = 2;
const int invalidDataExitCode = 3;

var jsonOptions = new JsonSerializerOptions
{
    WriteIndented = true,
    Converters = { new JsonStringEnumConverter() }
};

try
{
    var evaluationFile = WorkspacePathLocator.FindEvaluationFile(args.FirstOrDefault());
    var knowledgeRoot = WorkspacePathLocator.FindKnowledgeRoot(args.Skip(1).FirstOrDefault());
    var suiteFile = args.Skip(2).FirstOrDefault()
                    ?? Path.Combine(Path.GetDirectoryName(evaluationFile)!, "evaluation-suite.json");
    var cases = await EvaluationCaseLoader.LoadAsync(evaluationFile, suiteFile);

    using var repository = new MarkdownKnowledgeRepository(knowledgeRoot);
    await repository.InitializeAsync();
    var needsRetrievalFixture = cases.Any(item => item.Oracle?.FixtureId is
        BuiltInRetrievedContentFixtures.CleanFixtureId or BuiltInRetrievedContentFixtures.MixedFixtureId);
    var retrievalFixtureRoot = Path.Combine(Path.GetDirectoryName(evaluationFile)!, "fixtures", "retrieval-safety",
        "knowledge");
    using var retrievalRepository = needsRetrievalFixture ? new MarkdownKnowledgeRepository(retrievalFixtureRoot) : null;
    if (retrievalRepository is not null) await retrievalRepository.InitializeAsync();

    using IChatClient chatClient = new DeterministicGroundedChatClient();
    var queryNormalizer = new RuleBasedQueryNormalizer();
    var retrievalDefinitions = BuiltInRetrievedContentFixtures.Create();
    var retrievalSubjects = retrievalDefinitions.Select(item => $"{item.TenantId}\u001f{item.SubjectId}")
        .ToHashSet(StringComparer.Ordinal);
    var routingRepository = new EvaluationRoutingKnowledgeRepository(repository, retrievalRepository, retrievalSubjects);
    var recordingRepository = new RecordingKnowledgeRepository(routingRepository);
    var recordingRetrievalSafety = new RecordingRetrievedContentSafetyService(
        new RuleBasedRetrievedContentSafetyService());
    var recordingReranker = new RecordingEvidenceReranker(new LexicalEvidenceReranker());
    var recordingComposer = new RecordingAnswerComposer(new AgentFrameworkAnswerComposer(chatClient));
    var service = new TrustedQuestionService(recordingRepository, queryNormalizer, new RuleBasedInputSafetyService(),
        recordingRetrievalSafety, recordingReranker, new RuleBasedEvidenceSufficiencyEvaluator(),
        recordingComposer, new RuleBasedOutputSafetyService(),
        new InMemoryTraceSink(), new TrustedQuestionOptions(), new EmptyMemoryContextProvider());
    var aclFixtureRegistry = new KnowledgeAclEvaluationFixtureRegistry(recordingRepository, repository, queryNormalizer,
        BuiltInEvaluationFixtures.Create());
    var fixtureRoutes = new Dictionary<string, IEvaluationFixtureRegistry>(StringComparer.Ordinal)
    {
        [BuiltInEvaluationFixtures.AclRestrictedWebPolicyAllowed] = aclFixtureRegistry,
        [BuiltInEvaluationFixtures.AclRestrictedWebPolicyDenied] = aclFixtureRegistry
    };

    if (retrievalRepository is not null)
    {
        var retrievalFixtureRegistry = new RetrievedContentSafetyEvaluationFixtureRegistry(
            recordingRepository, retrievalRepository, queryNormalizer, recordingRetrievalSafety, recordingReranker,
            recordingComposer, retrievalDefinitions);
        fixtureRoutes[BuiltInRetrievedContentFixtures.CleanFixtureId] = retrievalFixtureRegistry;
        fixtureRoutes[BuiltInRetrievedContentFixtures.MixedFixtureId] = retrievalFixtureRegistry;
    }

    var runner = new EvaluationRunner(new TrustedQuestionEvaluationTarget(service),
        new RoutingEvaluationFixtureRegistry(fixtureRoutes));
    var report = EvaluationReportBuilder.Build(await runner.RunAsync(cases));

    Console.WriteLine(JsonSerializer.Serialize(new
    {
        report.GeneratedAt,
        report.Total,
        Knowledge = repository.Statistics,
        report.Passed,
        report.Failed,
        report.NotReady,
        report.ActionAccuracy,
        report.ActionCoverage,
        report.CitationRecall,
        report.OracleCoverage,
        report.QualityGate,
        report.ByCategory,
        report.NonPassingCases
    }, jsonOptions));
    return report.QualityGate.Passed ? 0 : qualityFailureExitCode;
}
catch (EvaluationDataException exception)
{
    Console.Error.WriteLine(JsonSerializer.Serialize(new
    {
        error = "invalid_evaluation_data",
        exception.Code,
        message = exception.Message
    }, jsonOptions));
    return invalidDataExitCode;
}
