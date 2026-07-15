using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using AiMentor.Evaluation;
using AiMentor.Infrastructure;

const int qualityFailureExitCode = 2;
const int invalidDataExitCode = 3;
var jsonOptions = new JsonSerializerOptions { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

try
{
    var compare = args.Contains("--compare", StringComparer.OrdinalIgnoreCase);
    var outputIndex = Array.FindIndex(args, item => string.Equals(item, "--output", StringComparison.OrdinalIgnoreCase));
    if (outputIndex >= 0 && (outputIndex + 1 >= args.Length || args[outputIndex + 1].StartsWith("--", StringComparison.Ordinal)))
        throw new EvaluationDataException("EVALUATION_OUTPUT_PATH_MISSING", "--output 后必须提供报告路径。");
    var outputPath = outputIndex >= 0 ? args[outputIndex + 1] : null;
    var positional = args.Where((_, index) => !string.Equals(args[index], "--compare", StringComparison.OrdinalIgnoreCase)
                                                   && (outputIndex < 0
                                                       || index != outputIndex && index != outputIndex + 1)).ToArray();
    var evaluationFile = WorkspacePathLocator.FindEvaluationFile(positional.FirstOrDefault());
    var knowledgeRoot = WorkspacePathLocator.FindKnowledgeRoot(positional.Skip(1).FirstOrDefault());
    var suiteFile = positional.Skip(2).FirstOrDefault()
                    ?? Path.Combine(Path.GetDirectoryName(evaluationFile)!, "evaluation-suite.json");
    var cases = await EvaluationCaseLoader.LoadAsync(evaluationFile, suiteFile);

    using var repository = new MarkdownKnowledgeRepository(knowledgeRoot);
    await repository.InitializeAsync();
    var needsRetrievalFixture = cases.Any(item => item.Oracle?.FixtureId is
        BuiltInRetrievedContentFixtures.CleanFixtureId or BuiltInRetrievedContentFixtures.MixedFixtureId);
    var retrievalRoot = Path.Combine(Path.GetDirectoryName(evaluationFile)!, "fixtures", "retrieval-safety", "knowledge");
    using var retrievalRepository = needsRetrievalFixture ? new MarkdownKnowledgeRepository(retrievalRoot) : null;
    if (retrievalRepository is not null) await retrievalRepository.InitializeAsync();

    if (compare)
    {
        var baselineModel = new ModelProviderOptions();
        var baselineEmbedding = new EmbeddingProviderOptions();
        var baselineReranker = new RerankerProviderOptions { Provider = AiProviderKind.Lexical };
        using var baseline = ProviderEvaluationRuntime.Create(repository, retrievalRepository, baselineModel,
            baselineEmbedding, baselineReranker);

        ProviderEvaluationRuntime? candidate = null;
        Exception? candidateConfigurationError = null;
        ProviderComparisonDescriptor candidateDescriptor;
        try
        {
            var candidateModel = RemoteOptions("MODEL");
            var candidateEmbedding = RemoteEmbeddingOptions();
            var candidateReranker = RemoteRerankerOptions();
            candidateDescriptor = Descriptor(candidateModel, candidateEmbedding, candidateReranker);
            candidate = ProviderEvaluationRuntime.Create(repository, retrievalRepository, candidateModel,
                candidateEmbedding, candidateReranker);
        }
        catch (Exception exception) when (exception is AiProviderConfigurationException or ArgumentException)
        {
            candidateConfigurationError = exception;
            candidateDescriptor = UnsafeCandidateDescriptor();
        }

        try
        {
            var suiteHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(suiteFile)));
            var price = ReadPrice();
            var report = await ProviderComparisonRunner.RunAsync(suiteHash, cases, baseline.Descriptor,
                baseline.ExecuteAsync, candidateDescriptor,
                candidate is null
                    ? (_, _) => Task.FromException<ProviderCaseExecution>(candidateConfigurationError!)
                    : candidate.ExecuteAsync,
                price);
            var json = JsonSerializer.Serialize(report, jsonOptions);
            Console.WriteLine(json);
            if (!string.IsNullOrWhiteSpace(outputPath)) await File.WriteAllTextAsync(outputPath, json);
            return report.Passed ? 0 : qualityFailureExitCode;
        }
        finally
        {
            candidate?.Dispose();
        }
    }

    var model = ReadOptionalModelOptions();
    using var runtime = ProviderEvaluationRuntime.Create(repository, retrievalRepository, model,
        new EmbeddingProviderOptions(), new RerankerProviderOptions { Provider = AiProviderKind.Lexical });
    var results = await runtime.Runner.RunAsync(cases);
    var normalReport = EvaluationReportBuilder.Build(results);
    var normalJson = JsonSerializer.Serialize(new
    {
        normalReport.GeneratedAt, normalReport.Total, Knowledge = repository.Statistics, normalReport.Passed,
        normalReport.Failed, normalReport.NotReady, normalReport.ActionAccuracy, normalReport.ActionCoverage,
        normalReport.CitationRecall, normalReport.OracleCoverage, normalReport.QualityGate, normalReport.ByCategory,
        normalReport.NonPassingCases
    }, jsonOptions);
    Console.WriteLine(normalJson);
    if (!string.IsNullOrWhiteSpace(outputPath)) await File.WriteAllTextAsync(outputPath, normalJson);
    return normalReport.QualityGate.Passed ? 0 : qualityFailureExitCode;
}
catch (EvaluationDataException exception)
{
    Console.Error.WriteLine(JsonSerializer.Serialize(new
        { error = "invalid_evaluation_data", exception.Code, message = exception.Message }, jsonOptions));
    return invalidDataExitCode;
}

static ModelProviderOptions ReadOptionalModelOptions()
{
    var provider = Environment.GetEnvironmentVariable("AIMENTOR_EVALUATION_MODEL_PROVIDER");
    return string.IsNullOrWhiteSpace(provider) || provider.Equals("Deterministic", StringComparison.OrdinalIgnoreCase)
        ? new ModelProviderOptions()
        : RemoteOptions("MODEL");
}

static ModelProviderOptions RemoteOptions(string component)
{
    var prefix = $"AIMENTOR_EVALUATION_{component}_";
    var providerText = Environment.GetEnvironmentVariable(prefix + "PROVIDER");
    if (string.IsNullOrWhiteSpace(providerText))
        throw new AiProviderConfigurationException("EVALUATION_PROVIDER_CONFIGURATION_MISSING",
            $"真实对比缺少 {component} provider 配置。");
    if (!Enum.TryParse<AiProviderKind>(providerText, true, out var provider))
        throw new AiProviderConfigurationException("EVALUATION_PROVIDER_CONFIGURATION_INVALID",
            $"{component} provider 配置不受支持。");
    return new ModelProviderOptions
    {
        Provider = provider, Endpoint = Environment.GetEnvironmentVariable(prefix + "ENDPOINT"),
        ApiKey = Environment.GetEnvironmentVariable(prefix + "API_KEY"),
        Model = Environment.GetEnvironmentVariable(prefix + "ID"), TimeoutSeconds = 30, MaximumRetries = 2
    };
}

static EmbeddingProviderOptions RemoteEmbeddingOptions()
{
    var raw = RemoteOptions("EMBEDDING");
    return new EmbeddingProviderOptions
    {
        Provider = raw.Provider, Endpoint = raw.Endpoint, ApiKey = raw.ApiKey, Model = raw.Model,
        TimeoutSeconds = raw.TimeoutSeconds, MaximumRetries = raw.MaximumRetries,
        Dimensions = ParsePositiveInt("AIMENTOR_EVALUATION_EMBEDDING_DIMENSIONS"),
        IndexVersion = Environment.GetEnvironmentVariable("AIMENTOR_EVALUATION_EMBEDDING_INDEX_VERSION")
                       ?? throw new AiProviderConfigurationException("EVALUATION_PROVIDER_CONFIGURATION_MISSING",
                           "真实对比缺少 Embedding index version。")
    };
}

static RerankerProviderOptions RemoteRerankerOptions()
{
    var raw = RemoteOptions("RERANKER");
    return new RerankerProviderOptions
    {
        Provider = raw.Provider, Endpoint = raw.Endpoint, ApiKey = raw.ApiKey, Model = raw.Model,
        TimeoutSeconds = raw.TimeoutSeconds, MaximumRetries = raw.MaximumRetries
    };
}

static int ParsePositiveInt(string name) => int.TryParse(Environment.GetEnvironmentVariable(name), out var value)
                                            && value > 0 ? value : throw new AiProviderConfigurationException(
    "EVALUATION_PROVIDER_CONFIGURATION_MISSING", $"真实对比缺少有效的 {name}。");

static ProviderComparisonDescriptor Descriptor(ModelProviderOptions model, EmbeddingProviderOptions embedding,
    RerankerProviderOptions reranker) => new(model.Provider.ToString(), model.Model!, embedding.Provider.ToString(),
    embedding.Model!, embedding.IndexVersion, reranker.Provider.ToString(), reranker.Model!);

static ProviderComparisonDescriptor UnsafeCandidateDescriptor() => new(
    Environment.GetEnvironmentVariable("AIMENTOR_EVALUATION_MODEL_PROVIDER") ?? "NotReady",
    Environment.GetEnvironmentVariable("AIMENTOR_EVALUATION_MODEL_ID") ?? "NotReady",
    Environment.GetEnvironmentVariable("AIMENTOR_EVALUATION_EMBEDDING_PROVIDER") ?? "NotReady",
    Environment.GetEnvironmentVariable("AIMENTOR_EVALUATION_EMBEDDING_ID") ?? "NotReady",
    Environment.GetEnvironmentVariable("AIMENTOR_EVALUATION_EMBEDDING_INDEX_VERSION") ?? "NotReady",
    Environment.GetEnvironmentVariable("AIMENTOR_EVALUATION_RERANKER_PROVIDER") ?? "NotReady",
    Environment.GetEnvironmentVariable("AIMENTOR_EVALUATION_RERANKER_ID") ?? "NotReady");

static VersionedTokenPrice? ReadPrice()
{
    var version = Environment.GetEnvironmentVariable("AIMENTOR_EVALUATION_PRICE_VERSION");
    if (string.IsNullOrWhiteSpace(version)) return null;
    if (!decimal.TryParse(Environment.GetEnvironmentVariable("AIMENTOR_EVALUATION_INPUT_PRICE_PER_MILLION"),
            out var input) || !decimal.TryParse(
            Environment.GetEnvironmentVariable("AIMENTOR_EVALUATION_OUTPUT_PRICE_PER_MILLION"), out var output))
        throw new EvaluationDataException("EVALUATION_PRICE_INVALID", "显式价格版本必须同时提供输入和输出单价。");
    return new VersionedTokenPrice(version, input, output);
}
