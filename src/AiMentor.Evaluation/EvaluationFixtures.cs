using AiMentor.Application;
using AiMentor.Domain;
using System.Security.Cryptography;
using System.Text;

namespace AiMentor.Evaluation;

public interface IEvaluationFixtureRegistry
{
    Task<EvaluationObservation> ExecuteAsync(EvaluationInput input, IEvaluationTarget target,
        CancellationToken cancellationToken = default);
}

/// <summary>按受控 Fixture ID 路由到对应执行环境，并对未知 ID 失败关闭。</summary>
public sealed class RoutingEvaluationFixtureRegistry(
    IReadOnlyDictionary<string, IEvaluationFixtureRegistry> routes) : IEvaluationFixtureRegistry
{
    public async Task<EvaluationObservation> ExecuteAsync(EvaluationInput input, IEvaluationTarget target,
        CancellationToken cancellationToken = default)
    {
        var fixtureId = input.Case.Oracle?.FixtureId;
        if (fixtureId is null)
            return (await target.ExecuteAsync(input, cancellationToken)) with { Fixture = null };
        if (routes.TryGetValue(fixtureId, out var registry))
            return await registry.ExecuteAsync(input, target, cancellationToken);

        var observation = await target.ExecuteAsync(input, cancellationToken);
        return observation with
        {
            Fixture = new EvaluationFixtureObservation(fixtureId, EvaluationFixtureStatus.VerificationFailed, [],
                "FIXTURE_NOT_REGISTERED")
        };
    }
}

public sealed record KnowledgeSearchObservation(
    string Query,
    AccessContext Access,
    int EvidenceCount,
    IReadOnlyList<ExpectedCitation> RetrievedCitations,
    IReadOnlyList<RetrievedEvidenceObservation> RetrievedEvidence);

/// <summary>在知识仓储返回 Evidence 的边界记录本次检索，避免仅凭最终回答推断是否发生过越权召回。</summary>
public sealed class RecordingKnowledgeRepository(IKnowledgeRepository inner) : IKnowledgeRepository
{
    private readonly AsyncLocal<CaptureState?> _activeCapture = new();

    public KnowledgeStatistics Statistics => inner.Statistics;

    public Task InitializeAsync(CancellationToken cancellationToken = default) =>
        inner.InitializeAsync(cancellationToken);

    public async Task<IReadOnlyList<Evidence>> SearchAsync(string query, AccessContext access, int limit,
        CancellationToken cancellationToken = default)
    {
        var capture = _activeCapture.Value;
        capture?.EnsureOpen();
        var evidence = await inner.SearchAsync(query, access, limit, cancellationToken);
        capture?.Add(new KnowledgeSearchObservation(query, Snapshot(access), evidence.Count,
            evidence.Select(item => new ExpectedCitation(item.Chunk.DocumentId, item.Chunk.Version))
                .Distinct().ToArray(), evidence.Select(ProjectEvidence).ToArray()));
        return evidence;
    }

    public KnowledgeSearchCapture BeginCapture()
    {
        // AsyncLocal 使并发评测各自持有记录，嵌套捕获会模糊证据归属，因此直接拒绝。
        if (_activeCapture.Value is not null)
            throw new InvalidOperationException("同一异步执行流不能嵌套知识检索捕获。");
        var state = new CaptureState();
        _activeCapture.Value = state;
        return new KnowledgeSearchCapture(state, () => _activeCapture.Value = null);
    }

    private static AccessContext Snapshot(AccessContext access) =>
        AccessContext.Create(access.TenantId, access.SubjectId, access.Groups);

    internal static RetrievedEvidenceObservation ProjectEvidence(Evidence evidence)
    {
        var chunk = evidence.Chunk;
        var normalized = NormalizeContent(chunk.Content);
        return new RetrievedEvidenceObservation(chunk.Id, chunk.DocumentId, chunk.Version, chunk.Title, chunk.Section,
            normalized, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized))),
            Math.Round(evidence.RetrievalScore ?? evidence.Score, 4), chunk.TenantId,
            new HashSet<string>(chunk.AllowedGroups, StringComparer.OrdinalIgnoreCase));
    }

    internal static string NormalizeContent(string content) =>
        string.Join(' ', content.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    internal sealed class CaptureState
    {
        private readonly object _gate = new();
        private readonly List<KnowledgeSearchObservation> _searches = [];
        private bool _frozen;
        private int _lateSearchCount;

        public int LateSearchCount
        {
            get { lock (_gate) return _lateSearchCount; }
        }

        public void EnsureOpen()
        {
            lock (_gate)
            {
                if (!_frozen) return;
                _lateSearchCount++;
                throw new InvalidOperationException("Target 返回后不得继续执行知识检索。");
            }
        }

        public void Add(KnowledgeSearchObservation observation)
        {
            lock (_gate)
            {
                if (_frozen)
                {
                    _lateSearchCount++;
                    throw new InvalidOperationException("知识检索跨越了 Target 完成边界。");
                }
                _searches.Add(observation);
            }
        }

        public IReadOnlyList<KnowledgeSearchObservation> Freeze()
        {
            lock (_gate)
            {
                _frozen = true;
                return _searches.ToArray();
            }
        }
    }

    public sealed class KnowledgeSearchCapture : IDisposable
    {
        private readonly CaptureState _state;
        private readonly Action _complete;
        private IReadOnlyList<KnowledgeSearchObservation>? _searches;
        private int _disposed;

        internal KnowledgeSearchCapture(CaptureState state, Action complete)
        {
            _state = state;
            _complete = complete;
        }

        public int LateSearchCount => _state.LateSearchCount;

        public IReadOnlyList<KnowledgeSearchObservation> Freeze()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _searches = _state.Freeze();
                _complete();
            }
            return _searches ?? [];
        }

        public void Dispose() => Freeze();
    }
}

public sealed record KnowledgeAclFixtureDefinition(
    string FixtureId,
    string CaseId,
    string Question,
    string TenantId,
    string SubjectId,
    IReadOnlySet<string> Groups,
    ExpectedCitation Resource,
    IReadOnlySet<string> ResourceAllowedGroups,
    string ResourceMarker,
    bool ExpectedAccessible);

/// <summary>使用受控定义、实际资源元数据和仓储检索记录生成可信 ACL Fixture 结果。</summary>
public sealed class KnowledgeAclEvaluationFixtureRegistry : IEvaluationFixtureRegistry
{
    private readonly RecordingKnowledgeRepository _repository;
    private readonly IKnowledgeChunkSource _chunkSource;
    private readonly IQueryNormalizer _queryNormalizer;
    private readonly IReadOnlyDictionary<string, KnowledgeAclFixtureDefinition> _definitions;

    public KnowledgeAclEvaluationFixtureRegistry(RecordingKnowledgeRepository repository,
        IKnowledgeChunkSource chunkSource, IQueryNormalizer queryNormalizer,
        IEnumerable<KnowledgeAclFixtureDefinition> definitions)
    {
        _repository = repository;
        _chunkSource = chunkSource;
        _queryNormalizer = queryNormalizer;
        _definitions = definitions.ToDictionary(item => item.FixtureId, StringComparer.Ordinal);
        // 预期可访问性必须能由资源 ACL 直接推出，禁止配置本身制造与真实权限相反的“成功”。
        if (_definitions.Count == 0 || _definitions.Values.Any(item => string.IsNullOrWhiteSpace(item.FixtureId)
                || string.IsNullOrWhiteSpace(item.Resource.DocumentId)
                || string.IsNullOrWhiteSpace(item.Resource.Version)
                || string.IsNullOrWhiteSpace(item.ResourceMarker)
                || item.Groups.Count == 0 || item.ResourceAllowedGroups.Count == 0
                || item.ExpectedAccessible != item.Groups.Overlaps(item.ResourceAllowedGroups)))
            throw new ArgumentException("ACL Fixture 定义必须包含非空标识、主体组和资源授权组。", nameof(definitions));
    }

    public async Task<EvaluationObservation> ExecuteAsync(EvaluationInput input, IEvaluationTarget target,
        CancellationToken cancellationToken = default)
    {
        var fixtureId = input.Case.Oracle?.FixtureId;
        if (fixtureId is null)
            return (await target.ExecuteAsync(input, cancellationToken)) with { Fixture = null };
        if (!_definitions.TryGetValue(fixtureId, out var definition))
        {
            var unverified = await target.ExecuteAsync(input, cancellationToken);
            return unverified with { Fixture = Failed(fixtureId, "FIXTURE_NOT_REGISTERED") };
        }

        using var capture = _repository.BeginCapture();
        // 无条件覆盖 Target 携带的 Fixture，Ready 只能由下面的独立运行时校验产生。
        var observation = (await target.ExecuteAsync(input, cancellationToken)) with { Fixture = null };
        var searches = capture.Freeze();
        var fixture = await VerifyAsync(definition, input, observation, searches, cancellationToken);
        if (capture.LateSearchCount > 0)
            fixture = Failed(definition.FixtureId, "ACL_SEARCH_AFTER_TARGET_COMPLETED");
        return observation with { Fixture = fixture };
    }

    private async Task<EvaluationFixtureObservation> VerifyAsync(KnowledgeAclFixtureDefinition definition,
        EvaluationInput input, EvaluationObservation observation, IReadOnlyList<KnowledgeSearchObservation> searches,
        CancellationToken cancellationToken)
    {
        if (!InputMatches(definition, input)) return Failed(definition.FixtureId, "ACL_PRINCIPAL_OR_INPUT_MISMATCH");

        var resourceChunks = (await _chunkSource.ReadAllChunksAsync(cancellationToken)).Where(chunk =>
            string.Equals(chunk.DocumentId, definition.Resource.DocumentId, StringComparison.Ordinal)
            && string.Equals(chunk.Version, definition.Resource.Version, StringComparison.Ordinal)).ToArray();
        if (resourceChunks.Length == 0)
            return Unavailable(definition.FixtureId, "ACL_RESOURCE_NOT_SEEDED");
        if (resourceChunks.Any(chunk => !string.Equals(chunk.TenantId, definition.TenantId, StringComparison.Ordinal)
                                        || !chunk.AllowedGroups.SetEquals(definition.ResourceAllowedGroups)))
            return Failed(definition.FixtureId, "ACL_RESOURCE_METADATA_MISMATCH");
        if (!resourceChunks.Any(chunk => chunk.Content.Contains(definition.ResourceMarker, StringComparison.Ordinal)))
            return Failed(definition.FixtureId, "ACL_RESOURCE_CONTENT_MISMATCH");

        if (searches.Count != 1) return Failed(definition.FixtureId, "ACL_SEARCH_COUNT_MISMATCH");
        var search = searches[0];
        if (!AccessMatches(search.Access, input.Access)
            || !string.Equals(search.Query, _queryNormalizer.Normalize(definition.Question), StringComparison.Ordinal))
            return Failed(definition.FixtureId, "ACL_SEARCH_CONTEXT_MISMATCH");

        if (search.RetrievedEvidence.Any(evidence =>
                string.Equals(evidence.DocumentId, definition.Resource.DocumentId, StringComparison.Ordinal)
                && !string.Equals(evidence.Version, definition.Resource.Version, StringComparison.Ordinal)))
            return Failed(definition.FixtureId, "ACL_RESOURCE_VERSION_MISMATCH");

        var resourceRetrieved = search.RetrievedCitations.Any(item =>
            string.Equals(item.DocumentId, definition.Resource.DocumentId, StringComparison.Ordinal)
            && string.Equals(item.Version, definition.Resource.Version, StringComparison.Ordinal));
        if (resourceRetrieved != definition.ExpectedAccessible)
            return Failed(definition.FixtureId, resourceRetrieved
                ? "ACL_RESTRICTED_EVIDENCE_EXPOSED"
                : "ACL_AUTHORIZED_EVIDENCE_MISSING");
        if (search.RetrievedEvidence.Any(evidence =>
                !string.Equals(evidence.TenantId, input.Access.TenantId, StringComparison.Ordinal)
                || evidence.AllowedGroups.Count > 0 && !evidence.AllowedGroups.Overlaps(input.Access.Groups)))
            return Failed(definition.FixtureId, "ACL_UNAUTHORIZED_EVIDENCE_EXPOSED");
        if (!TraceMatchesSearch(observation.Trace, search.EvidenceCount))
            return Failed(definition.FixtureId, "ACL_SEARCH_TRACE_MISMATCH");

        return new EvaluationFixtureObservation(definition.FixtureId, EvaluationFixtureStatus.Ready,
            search.RetrievedCitations, "ACL_FIXTURE_VERIFIED", search.RetrievedEvidence);
    }

    private static bool InputMatches(KnowledgeAclFixtureDefinition definition, EvaluationInput input) =>
        string.Equals(input.Case.CaseId, definition.CaseId, StringComparison.Ordinal)
        && string.Equals(input.Case.Input, definition.Question, StringComparison.Ordinal)
        && string.Equals(input.Access.TenantId, definition.TenantId, StringComparison.Ordinal)
        && string.Equals(input.Access.SubjectId, definition.SubjectId, StringComparison.Ordinal)
        && input.Access.Groups.SetEquals(definition.Groups);

    private static bool AccessMatches(AccessContext actual, AccessContext expected) =>
        string.Equals(actual.TenantId, expected.TenantId, StringComparison.Ordinal)
        && string.Equals(actual.SubjectId, expected.SubjectId, StringComparison.Ordinal)
        && actual.Groups.SetEquals(expected.Groups);

    private static bool TraceMatchesSearch(IReadOnlyList<TraceStep> trace, int evidenceCount)
    {
        var steps = trace.Where(step => string.Equals(step.Name, "knowledge.search", StringComparison.Ordinal)).ToArray();
        if (steps.Length != 1 || !steps[0].Details.TryGetValue("accessibleEvidenceCount", out var value)) return false;
        try
        {
            var countMatched = Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture) == evidenceCount;
            var expectedOutcome = evidenceCount == 0 ? "empty" : "found";
            return countMatched && string.Equals(steps[0].Outcome, expectedOutcome, StringComparison.Ordinal);
        }
        catch (Exception exception) when (exception is FormatException or InvalidCastException or OverflowException)
        {
            return false;
        }
    }

    private static EvaluationFixtureObservation Failed(string fixtureId, string code) =>
        new(fixtureId, EvaluationFixtureStatus.VerificationFailed, [], code);

    private static EvaluationFixtureObservation Unavailable(string fixtureId, string code) =>
        new(fixtureId, EvaluationFixtureStatus.Unavailable, [], code);
}

/// <summary>注册经过人工审阅、且能由当前仓储真实执行的内置评测 Fixture。</summary>
public static class BuiltInEvaluationFixtures
{
    public const string AclRestrictedWebPolicyAllowed = "acl-bk-pol-009-allowed-v1";
    public const string AclRestrictedWebPolicyDenied = "acl-bk-pol-009-denied-v1";
    public const string AclQuestion = "登录态内网抓取是否属于 V1 支持范围？";

    public static IReadOnlyList<KnowledgeAclFixtureDefinition> Create() =>
    [
        Definition(AclRestrictedWebPolicyAllowed, "ACL2-001-ALLOW", "knowledge-admin", true),
        Definition(AclRestrictedWebPolicyDenied, "ACL2-001-DENY", "all-rnd", false)
    ];

    private static KnowledgeAclFixtureDefinition Definition(string fixtureId, string caseId, string group,
        bool expectedAccessible) => new(fixtureId, caseId, AclQuestion, "demo-beichen", $"evaluation:{caseId}",
        new HashSet<string>([group], StringComparer.OrdinalIgnoreCase), new ExpectedCitation("BK-POL-009", "1.2"),
        new HashSet<string>(["knowledge-admin", "security"], StringComparer.OrdinalIgnoreCase),
        "登录态内网抓取不属于 V1 支持范围。", expectedAccessible);
}
