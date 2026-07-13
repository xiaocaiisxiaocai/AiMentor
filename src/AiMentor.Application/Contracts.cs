using AiMentor.Domain;

namespace AiMentor.Application;

/// <summary>在租户与 ACL 前置过滤约束下初始化并检索知识证据。</summary>
public interface IKnowledgeRepository
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Evidence>> SearchAsync(string query, AccessContext access, int limit, CancellationToken cancellationToken = default);
    KnowledgeStatistics Statistics { get; }
}

/// <summary>移除问句模板噪声，同时保留检索所需实体和值类型信息。</summary>
public interface IQueryNormalizer
{
    string Normalize(string question);
}

/// <summary>向索引适配器提供带来源和 ACL 的完整知识分块。</summary>
public interface IKnowledgeChunkSource
{
    Task<IReadOnlyList<KnowledgeChunk>> ReadAllChunksAsync(CancellationToken cancellationToken = default);
}

/// <summary>抽象文本向量生成，使沙箱与生产嵌入模型可替换。</summary>
public interface ITextEmbeddingGenerator
{
    int Dimensions { get; }
    Task<float[]> GenerateAsync(string text, CancellationToken cancellationToken = default);
}

/// <summary>公开不包含文档正文的知识库规模统计。</summary>
public sealed record KnowledgeStatistics(int Documents, int Chunks);

/// <summary>在检索和模型调用前审核用户输入。</summary>
public interface IInputSafetyService
{
    string PolicyVersion { get; }
    SafetyDecision Review(string input);
}

/// <summary>把检索内容视为不可信数据并隔离恶意指令或凭证。</summary>
public interface IRetrievedContentSafetyService
{
    string PolicyVersion { get; }
    RetrievedContentReview Review(IReadOnlyList<Evidence> evidence);
}

/// <summary>记录被隔离分块的标识和无敏感正文的原因代码。</summary>
public sealed record RetrievedContentRejection(string ChunkId, string Code);

/// <summary>拆分可继续使用的证据和已隔离内容。</summary>
public sealed record RetrievedContentReview(
    IReadOnlyList<Evidence> AcceptedEvidence,
    IReadOnlyList<RetrievedContentRejection> Rejections);

/// <summary>依据服务器风险、身份、参数和网络目标审核工具调用。</summary>
public interface IToolInvocationSafetyService
{
    string PolicyVersion { get; }
    SafetyDecision Review(ToolInvocationRequest request, AccessContext access);
}

/// <summary>供安全策略审核的最小工具调用投影。</summary>
public sealed record ToolInvocationRequest(
    string ToolName,
    ToolOperationRisk Risk,
    IReadOnlyDictionary<string, object?> Arguments);

/// <summary>审核模型输出是否安全、带有效引用且能由证据落地。</summary>
public interface IOutputSafetyService
{
    string PolicyVersion { get; }
    SafetyDecision Review(string answer, IReadOnlyList<Evidence> evidence, IReadOnlyList<Citation> citations);
}

/// <summary>使用证据和只读记忆上下文编排回答，不负责生成引用。</summary>
public interface IAnswerComposer
{
    Task<string> ComposeAsync(string question, IReadOnlyList<Evidence> evidence,
        IReadOnlyList<MemoryContextItem> memories, CancellationToken cancellationToken = default);
}

/// <summary>在不篡改原始检索分的前提下调整提供给模型的证据顺序。</summary>
public interface IEvidenceReranker
{
    Task<IReadOnlyList<Evidence>> RerankAsync(string question, IReadOnlyList<Evidence> evidence,
        CancellationToken cancellationToken = default);
}

/// <summary>在调用回答模型前判断证据能否可靠承载答案。</summary>
public interface IEvidenceSufficiencyEvaluator
{
    EvidenceAssessment Evaluate(string question, IReadOnlyList<Evidence> evidence, double minimumScore);
}

/// <summary>提供可解释的证据充分性结论和置信度。</summary>
public sealed record EvidenceAssessment(bool IsSufficient, double Confidence, string Code, string Explanation);

/// <summary>持久化或转发不含敏感正文的运行轨迹。</summary>
public interface ITraceSink
{
    Task WriteAsync(string runId, IReadOnlyList<TraceStep> trace, CancellationToken cancellationToken = default);
}

/// <summary>公开从输入审核到引用审核的完整可信问答用例。</summary>
public interface ITrustedQuestionService
{
    Task<TrustedAnswer> AskAsync(TrustedQuestion question, CancellationToken cancellationToken = default);
}

/// <summary>配置召回数量、证据阈值和最小证据数。</summary>
public sealed class TrustedQuestionOptions
{
    public int SearchLimit { get; init; } = 5;
    public double MinimumTopScore { get; init; } = 0.25;
    public int MinimumEvidenceCount { get; init; } = 1;
}
