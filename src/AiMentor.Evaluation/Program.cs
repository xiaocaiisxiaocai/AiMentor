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
    using IChatClient chatClient = new DeterministicGroundedChatClient();
    var service = new TrustedQuestionService(repository, new RuleBasedQueryNormalizer(), new RuleBasedInputSafetyService(),
        new RuleBasedRetrievedContentSafetyService(),
        new LexicalEvidenceReranker(), new RuleBasedEvidenceSufficiencyEvaluator(),
        new AgentFrameworkAnswerComposer(chatClient), new RuleBasedOutputSafetyService(),
        new InMemoryTraceSink(), new TrustedQuestionOptions(), new EmptyMemoryContextProvider());
    var runner = new EvaluationRunner(new TrustedQuestionEvaluationTarget(service));
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
