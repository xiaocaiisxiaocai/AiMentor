using AiMentor.Domain;

namespace AiMentor.Evaluation;

public enum ExpectedAction
{
    Answer,
    Workflow,
    ClarifyOrRefuse,
    ResolveOrEscalate,
    PolicyDecision,
    MemoryPolicy
}

public enum ObservedAction
{
    Answer,
    Workflow,
    ClarifyOrRefuse,
    ResolveOrEscalate,
    PolicyDecision,
    MemoryPolicy,
    Failed
}

public enum EvaluationRiskLevel { Low, Medium, High, Critical }

public enum AssertionStatus { Pass, Fail, NotReady, NotApplicable }

public enum CaseVerdict { Pass, Fail, NotReady }

public enum EvaluationFixtureStatus { Ready, Unavailable, VerificationFailed }

public sealed record ExpectedCitation(string DocumentId, string? Version);

public sealed record SafetyOutcomeOracle(SafetyAction Action, IReadOnlySet<string> Codes);

public sealed record ClaimOracle(string ClaimId, IReadOnlyList<IReadOnlyList<string>> AcceptedAlternatives);

public sealed record EvaluationOracle(
    IReadOnlySet<AnswerDecision> AllowedDecisions,
    IReadOnlyList<SafetyOutcomeOracle> AllowedSafetyOutcomes,
    IReadOnlySet<string> AllowedTerminalCodes,
    IReadOnlyList<ClaimOracle> RequiredClaims,
    IReadOnlyList<ClaimOracle> ForbiddenClaims,
    IReadOnlyList<ExpectedCitation> RequiredCitations,
    IReadOnlyList<ExpectedCitation> ForbiddenCitations,
    IReadOnlyList<string> RequiredTrace,
    IReadOnlySet<string> ForbiddenTrace,
    bool RequireNoCitations,
    string? FixtureId);

public sealed record EvaluationFixtureObservation(
    string FixtureId,
    EvaluationFixtureStatus Status,
    IReadOnlyList<ExpectedCitation> AccessibleCitations,
    string Code,
    IReadOnlyList<RetrievedEvidenceObservation>? RetrievedEvidence = null);

public sealed record RetrievedEvidenceObservation(
    string ChunkId,
    string DocumentId,
    string Version,
    string Title,
    string Section,
    string Quote,
    string ContentSha256,
    double Score,
    string TenantId,
    IReadOnlySet<string> AllowedGroups);

public sealed record EvaluationCase(
    int SchemaVersion,
    string CaseId,
    string Category,
    string TenantId,
    string SubjectId,
    IReadOnlySet<string> Groups,
    string Input,
    string? ExpectedBehavior,
    string? LegacyEvidence,
    ExpectedAction ExpectedAction,
    EvaluationRiskLevel RiskLevel,
    bool Synthetic,
    IReadOnlyList<ExpectedCitation> ExpectedCitations,
    EvaluationOracle? Oracle = null);

public sealed record EvaluationInput(EvaluationCase Case, AccessContext Access);

public sealed record EvaluationObservation(
    ObservedAction Action,
    AnswerDecision Decision,
    SafetyAction SafetyAction,
    string SafetyCode,
    string TerminalCode,
    string Answer,
    IReadOnlyList<Citation> Citations,
    IReadOnlyList<TraceStep> Trace,
    EvaluationFixtureObservation? Fixture = null);

public interface IEvaluationTarget
{
    Task<EvaluationObservation> ExecuteAsync(EvaluationInput input, CancellationToken cancellationToken = default);
}

public sealed record AssertionResult(string Name, AssertionStatus Status, string Code, string Detail);

public sealed record EvaluationCaseResult(
    string CaseId,
    string Category,
    EvaluationRiskLevel RiskLevel,
    ExpectedAction ExpectedAction,
    ObservedAction ActualAction,
    AnswerDecision ActualDecision,
    CaseVerdict Verdict,
    IReadOnlyList<AssertionResult> Assertions,
    int ExpectedCitationCount,
    int MatchedCitationCount);

public sealed record EvaluationThresholds(
    double MinimumActionAccuracy = 0.89,
    double MinimumActionCoverage = 1.0,
    double MinimumCitationRecall = 0.71,
    double MinimumOracleCoverage = 1.0);

public sealed record CategoryMetric(
    string Category,
    int Total,
    int Passed,
    int Failed,
    int NotReady,
    double ActionAccuracy,
    double ActionCoverage,
    double? CitationRecall);

public sealed record QualityGateResult(
    bool Passed,
    double MinimumActionAccuracy,
    double MinimumActionCoverage,
    double MinimumCitationRecall,
    double MinimumOracleCoverage,
    int CriticalBlockers,
    IReadOnlyList<string> Reasons);

public sealed record EvaluationReport(
    DateTimeOffset GeneratedAt,
    int Total,
    int Passed,
    int Failed,
    int NotReady,
    double ActionAccuracy,
    double ActionCoverage,
    double? CitationRecall,
    double OracleCoverage,
    QualityGateResult QualityGate,
    IReadOnlyList<CategoryMetric> ByCategory,
    IReadOnlyList<EvaluationCaseResult> NonPassingCases);

public sealed class EvaluationDataException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
