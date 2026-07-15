using AiMentor.Application;
using AiMentor.Infrastructure;
using Microsoft.Extensions.AI;

namespace AiMentor.Evaluation;

internal sealed class ProviderEvaluationRuntime : IDisposable
{
    private readonly IChatClient _chatClient;
    private readonly ITextEmbeddingGenerator _embedding;

    private ProviderEvaluationRuntime(EvaluationRunner runner, IChatClient chatClient,
        ITextEmbeddingGenerator embedding, ProviderComparisonDescriptor descriptor)
    {
        Runner = runner;
        _chatClient = chatClient;
        _embedding = embedding;
        Descriptor = descriptor;
    }

    public EvaluationRunner Runner { get; }
    public ProviderComparisonDescriptor Descriptor { get; }

    public async Task<ProviderCaseExecution> ExecuteAsync(EvaluationCase evaluationCase,
        CancellationToken cancellationToken)
    {
        // 使用固定无敏感探针实际验证候选 Embedding；原始评测输入必须先经过安全策略，不能为测量而旁路外传。
        var vector = await _embedding.GenerateAsync("AiMentor provider evaluation dimension probe", cancellationToken);
        if (vector.Length != _embedding.Dimensions)
            throw new InvalidOperationException("EVALUATION_EMBEDDING_DIMENSIONS_MISMATCH");
        var results = await Runner.RunAsync([evaluationCase], cancellationToken);
        if (results.Count != 1) throw new InvalidOperationException("EVALUATION_CASE_RESULT_COUNT_INVALID");
        var result = results[0];
        return new ProviderCaseExecution(result.Verdict);
    }

    public static ProviderEvaluationRuntime Create(MarkdownKnowledgeRepository repository,
        MarkdownKnowledgeRepository? retrievalRepository, ModelProviderOptions modelOptions,
        EmbeddingProviderOptions embeddingOptions, RerankerProviderOptions rerankerOptions)
    {
        var chatClient = AiProviderFactory.CreateChat(modelOptions);
        try
        {
            var embedding = AiProviderFactory.CreateEmbedding(embeddingOptions);
            var reranker = AiProviderFactory.CreateReranker(rerankerOptions);
            var queryNormalizer = new RuleBasedQueryNormalizer();
            var retrievalDefinitions = BuiltInRetrievedContentFixtures.Create();
            var retrievalSubjects = retrievalDefinitions.Select(item => $"{item.TenantId}\u001f{item.SubjectId}")
                .ToHashSet(StringComparer.Ordinal);
            var routingRepository = new EvaluationRoutingKnowledgeRepository(repository, retrievalRepository,
                retrievalSubjects);
            var recordingRepository = new RecordingKnowledgeRepository(routingRepository);
            var recordingInputSafety = new RecordingInputSafetyService(new RuleBasedInputSafetyService());
            var recordingRetrievalSafety = new RecordingRetrievedContentSafetyService(
                new RuleBasedRetrievedContentSafetyService());
            var recordingReranker = new RecordingEvidenceReranker(reranker);
            var recordingComposer = new RecordingAnswerComposer(new AgentFrameworkAnswerComposer(chatClient));
            var service = new TrustedQuestionService(recordingRepository, queryNormalizer, recordingInputSafety,
                recordingRetrievalSafety, recordingReranker, new RuleBasedEvidenceSufficiencyEvaluator(),
                recordingComposer, new RuleBasedOutputSafetyService(), new InMemoryTraceSink(),
                new TrustedQuestionOptions(), new EmptyMemoryContextProvider(), new RuleBasedCitationMapper(),
                new RuleBasedCitationVerifier(), new RuleBasedEvidenceConflictDetector());

            var aclRegistry = new KnowledgeAclEvaluationFixtureRegistry(recordingRepository, repository,
                queryNormalizer, BuiltInEvaluationFixtures.Create());
            var routes = new Dictionary<string, IEvaluationFixtureRegistry>(StringComparer.Ordinal)
            {
                [BuiltInEvaluationFixtures.AclRestrictedWebPolicyAllowed] = aclRegistry,
                [BuiltInEvaluationFixtures.AclRestrictedWebPolicyDenied] = aclRegistry
            };
            var operationalDefinitions = BuiltInOperationalEvaluationFixtures.Create();
            var operationalRegistry = new OperationalEvaluationFixtureRegistry(operationalDefinitions);
            foreach (var definition in operationalDefinitions) routes[definition.FixtureId] = operationalRegistry;
            var piiDefinitions = BuiltInPiiEvaluationFixtures.Create();
            var piiRegistry = new PiiPropagationEvaluationFixtureRegistry(recordingInputSafety, recordingRepository,
                recordingComposer, piiDefinitions);
            foreach (var definition in piiDefinitions) routes[definition.FixtureId] = piiRegistry;
            if (retrievalRepository is not null)
            {
                var retrievalRegistry = new RetrievedContentSafetyEvaluationFixtureRegistry(recordingRepository,
                    retrievalRepository, queryNormalizer, recordingRetrievalSafety, recordingReranker,
                    recordingComposer, retrievalDefinitions);
                routes[BuiltInRetrievedContentFixtures.CleanFixtureId] = retrievalRegistry;
                routes[BuiltInRetrievedContentFixtures.MixedFixtureId] = retrievalRegistry;
            }

            var descriptor = new ProviderComparisonDescriptor(modelOptions.Provider.ToString(),
                modelOptions.Model ?? modelOptions.Provider.ToString(), embeddingOptions.Provider.ToString(),
                embeddingOptions.Model ?? embeddingOptions.Provider.ToString(), embeddingOptions.IndexVersion,
                rerankerOptions.Provider.ToString(), rerankerOptions.Model ?? rerankerOptions.Provider.ToString());
            return new ProviderEvaluationRuntime(new EvaluationRunner(new TrustedQuestionEvaluationTarget(service),
                new RoutingEvaluationFixtureRegistry(routes)), chatClient, embedding, descriptor);
        }
        catch
        {
            chatClient.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        _chatClient.Dispose();
        if (_embedding is IDisposable disposable) disposable.Dispose();
    }
}
