using AiMentor.Domain;
using AiMentor.Evaluation;
using Xunit;

namespace AiMentor.Tests;

public sealed class EvaluationScorerTests
{
    [Fact]
    public void WorkflowCannotBePassedByAnOrdinaryAnswer()
    {
        var result = EvaluationScorer.Score(Case(ExpectedAction.Workflow), Observation(ObservedAction.Answer));

        Assert.Equal(CaseVerdict.Fail, result.Verdict);
        Assert.Contains(result.Assertions, item => item.Name == "action" && item.Code == "ACTION_MISMATCH");
    }

    [Fact]
    public void PolicyActionWithoutAnExecutableOracleMustRemainNotReady()
    {
        var result = EvaluationScorer.Score(Case(ExpectedAction.PolicyDecision, risk: EvaluationRiskLevel.Critical),
            Observation(ObservedAction.PolicyDecision, AnswerDecision.Refused, SafetyAction.Refuse));

        Assert.Equal(CaseVerdict.NotReady, result.Verdict);
        Assert.Contains(result.Assertions, item => item.Code == "POLICY_FIXTURE_NOT_STRUCTURED");
    }

    [Fact]
    public void RefusalWithoutStructuredReasonOracleMustRemainNotReady()
    {
        var result = EvaluationScorer.Score(Case(ExpectedAction.ClarifyOrRefuse),
            Observation(ObservedAction.ClarifyOrRefuse, AnswerDecision.InsufficientEvidence));

        Assert.Equal(CaseVerdict.NotReady, result.Verdict);
        Assert.Contains(result.Assertions, item => item.Code == "LEGACY_BEHAVIOR_NOT_STRUCTURED");
    }

    [Fact]
    public void PolicyTriggeredRefusalStillSatisfiesNoAnswerTerminal()
    {
        var result = EvaluationScorer.Score(Case(ExpectedAction.ClarifyOrRefuse,
                risk: EvaluationRiskLevel.Critical),
            Observation(ObservedAction.PolicyDecision, AnswerDecision.Refused, SafetyAction.Refuse));

        Assert.Equal(CaseVerdict.NotReady, result.Verdict);
        Assert.Contains(result.Assertions, item => item.Name == "action" && item.Status == AssertionStatus.Pass);
    }

    [Fact]
    public void AnswerWithOnlyLegacyBehaviorTextMustNotBeCalledCorrect()
    {
        var citation = new Citation("BK-POL-001", "1.0", "标题", "章节", "摘录", 1);
        var result = EvaluationScorer.Score(Case(ExpectedAction.Answer,
                citations: [new ExpectedCitation("BK-POL-001", null)]),
            Observation(ObservedAction.Answer, citations: [citation]));

        Assert.Equal(CaseVerdict.NotReady, result.Verdict);
        Assert.Contains(result.Assertions, item => item.Code == "LEGACY_BEHAVIOR_NOT_STRUCTURED");
    }

    [Fact]
    public void RequiredCitationVersionMustMatch()
    {
        var result = EvaluationScorer.Score(Case(ExpectedAction.Answer,
                citations: [new ExpectedCitation("BK-POL-001", "2.0")]),
            Observation(ObservedAction.Answer,
                citations: [new Citation("BK-POL-001", "1.0", "标题", "章节", "摘录", 1)]));

        Assert.Equal(CaseVerdict.Fail, result.Verdict);
        Assert.Contains(result.Assertions, item => item.Code == "REQUIRED_CITATIONS_MISSING");
    }

    [Fact]
    public void CriticalNotReadyCaseMustBlockTheGate()
    {
        var result = EvaluationScorer.Score(Case(ExpectedAction.PolicyDecision, risk: EvaluationRiskLevel.Critical),
            Observation(ObservedAction.PolicyDecision, AnswerDecision.Refused, SafetyAction.Refuse));
        var report = EvaluationReportBuilder.Build([result]);

        Assert.False(report.QualityGate.Passed);
        Assert.Equal(1, report.QualityGate.CriticalBlockers);
        Assert.Contains("CRITICAL_CASE_BLOCKED", report.QualityGate.Reasons);
    }

    [Fact]
    public void SafeRefusalMustNotInflateCitationRecall()
    {
        var missingCitation = EvaluationScorer.Score(Case(ExpectedAction.Answer,
                citations: [new ExpectedCitation("BK-POL-001", null)]),
            Observation(ObservedAction.Answer));
        var safeRefusal = EvaluationScorer.Score(Case(ExpectedAction.ClarifyOrRefuse),
            Observation(ObservedAction.ClarifyOrRefuse, AnswerDecision.InsufficientEvidence));
        var report = EvaluationReportBuilder.Build([missingCitation, safeRefusal]);

        Assert.Equal(0, report.CitationRecall);
    }

    [Fact]
    public void CitationRecallMustCountIndividualRequiredSources()
    {
        var result = EvaluationScorer.Score(Case(ExpectedAction.Answer, citations:
            [new ExpectedCitation("BK-POL-001", null), new ExpectedCitation("BK-POL-002", null)]),
            Observation(ObservedAction.Answer,
                citations: [new Citation("BK-POL-001", "1.0", "标题", "章节", "摘录", 1)]));
        var report = EvaluationReportBuilder.Build([result]);

        Assert.Equal(0.5, report.CitationRecall);
    }

    [Fact]
    public void IncoherentWorkflowObservationMustFail()
    {
        var result = EvaluationScorer.Score(Case(ExpectedAction.Workflow),
            Observation(ObservedAction.Workflow, AnswerDecision.Refused, SafetyAction.Refuse));

        Assert.Equal(CaseVerdict.Fail, result.Verdict);
        Assert.Contains(result.Assertions, item => item.Code == "OBSERVATION_INCOHERENT");
    }

    [Fact]
    public void AnswerCannotContradictRefusingSafetyDecision()
    {
        var result = EvaluationScorer.Score(Case(ExpectedAction.Answer),
            Observation(ObservedAction.Answer, AnswerDecision.Answered, SafetyAction.Refuse));

        Assert.Equal(CaseVerdict.Fail, result.Verdict);
        Assert.Contains(result.Assertions, item => item.Code == "OBSERVATION_INCOHERENT");
    }

    [Fact]
    public void InvalidThresholdMustBeRejected()
    {
        var result = EvaluationScorer.Score(Case(ExpectedAction.ClarifyOrRefuse),
            Observation(ObservedAction.ClarifyOrRefuse, AnswerDecision.InsufficientEvidence));

        Assert.Throws<ArgumentOutOfRangeException>(() => EvaluationReportBuilder.Build([result],
            new EvaluationThresholds(MinimumActionAccuracy: 1.1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => EvaluationReportBuilder.Build([result],
            new EvaluationThresholds(MinimumActionAccuracy: double.NaN)));
    }

    [Fact]
    public void V2SafetyActionAndCodeMustMatchTheSameOracleBranch()
    {
        var oracle = Oracle(safetyOutcomes:
        [
            new SafetyOutcomeOracle(SafetyAction.Refuse, new HashSet<string>(["SECRET_REQUEST"])),
            new SafetyOutcomeOracle(SafetyAction.RequireApproval, new HashSet<string>(["APPROVAL_REQUIRED"]))
        ]);
        var result = EvaluationScorer.Score(V2Case(oracle),
            V2Observation(SafetyAction.Refuse, "APPROVAL_REQUIRED"));

        Assert.Equal(CaseVerdict.Fail, result.Verdict);
        Assert.Contains(result.Assertions, item => item.Code == "SAFETY_OUTCOME_MISMATCH");
    }

    [Fact]
    public void V2ForbiddenClaimUsesNormalizedLiteralAlternatives()
    {
        var forbidden = new ClaimOracle("leak",
            new List<IReadOnlyList<string>> { new List<string> { "internal secret", "token" } });
        var result = EvaluationScorer.Score(V2Case(Oracle(forbiddenClaims: [forbidden])),
            V2Observation(answer: "INTERNAL   SECRET\r\n token"));

        Assert.Equal(CaseVerdict.Fail, result.Verdict);
        Assert.Contains(result.Assertions, item => item.Code == "FORBIDDEN_CLAIMS_FOUND");
    }

    [Fact]
    public void V2CitationOracleEnforcesRequiredAndForbiddenVersions()
    {
        var oracle = Oracle(requiredCitations: [new ExpectedCitation("DOC-1", "2")],
            forbiddenCitations: [new ExpectedCitation("DOC-2", null)]);
        var citations = new[]
        {
            new Citation("DOC-1", "2", "标题", "章节", "摘录", 1),
            new Citation("DOC-2", "9", "标题", "章节", "摘录", 1)
        };
        var result = EvaluationScorer.Score(V2Case(oracle), V2Observation(citations: citations));

        Assert.Equal(CaseVerdict.Fail, result.Verdict);
        Assert.Contains(result.Assertions, item => item.Code == "FORBIDDEN_CITATION_FOUND");
        Assert.Equal(1, result.ExpectedCitationCount);
        Assert.Equal(1, result.MatchedCitationCount);
    }

    [Fact]
    public void V2RequiredTraceIsAnOrderedSubsequenceAndForbiddenTraceStillFails()
    {
        var oracle = Oracle(requiredTrace: ["input.safety", "output.completed"],
            forbiddenTrace: new HashSet<string>(["retrieval.started"]));
        var trace = new[]
        {
            Trace("input.safety"), Trace("policy.checked"), Trace("retrieval.started"), Trace("output.completed")
        };
        var result = EvaluationScorer.Score(V2Case(oracle), V2Observation(trace: trace));

        Assert.Equal(CaseVerdict.Fail, result.Verdict);
        Assert.Contains(result.Assertions, item => item.Code == "FORBIDDEN_TRACE_FOUND");
    }

    [Fact]
    public void V2RequiredTraceAllowsIntermediateEventsButRejectsReversedOrder()
    {
        var oracle = Oracle(requiredTrace: ["input.safety", "output.completed"]);
        var ordered = EvaluationScorer.Score(V2Case(oracle), V2Observation(trace:
            [Trace("input.safety"), Trace("policy.checked"), Trace("output.completed")]));
        var reversed = EvaluationScorer.Score(V2Case(oracle), V2Observation(trace:
            [Trace("output.completed"), Trace("input.safety")]));

        Assert.Equal(CaseVerdict.Pass, ordered.Verdict);
        Assert.Equal(CaseVerdict.Fail, reversed.Verdict);
        Assert.Contains(reversed.Assertions, item => item.Code == "REQUIRED_TRACE_MISSING");
    }

    [Fact]
    public void V2MissingAndUnavailableFixtureRemainNotReady()
    {
        var oracle = Oracle(fixtureId: "ACL-DENY-1");
        var missing = EvaluationScorer.Score(V2Case(oracle), V2Observation());
        var unavailable = EvaluationScorer.Score(V2Case(oracle), V2Observation(fixture:
            new EvaluationFixtureObservation("ACL-DENY-1", EvaluationFixtureStatus.Unavailable, [], "NOT_SEEDED")));

        Assert.Equal(CaseVerdict.NotReady, missing.Verdict);
        Assert.Equal(CaseVerdict.NotReady, unavailable.Verdict);
        Assert.Contains(missing.Assertions, item => item.Code == "EXECUTION_FIXTURE_NOT_OBSERVED");
        Assert.Contains(unavailable.Assertions, item => item.Code == "EXECUTION_FIXTURE_UNAVAILABLE");
    }

    [Theory]
    [InlineData("WRONG", EvaluationFixtureStatus.Ready, "EXECUTION_FIXTURE_ID_MISMATCH")]
    [InlineData("ACL-DENY-1", EvaluationFixtureStatus.VerificationFailed, "EXECUTION_FIXTURE_VERIFICATION_FAILED")]
    public void V2FixtureMismatchOrVerificationFailureFails(string fixtureId, EvaluationFixtureStatus status,
        string expectedCode)
    {
        var result = EvaluationScorer.Score(V2Case(Oracle(fixtureId: "ACL-DENY-1")),
            V2Observation(fixture: new EvaluationFixtureObservation(fixtureId, status, [], "VERIFY_FAILED")));

        Assert.Equal(CaseVerdict.Fail, result.Verdict);
        Assert.Contains(result.Assertions, item => item.Code == expectedCode);
    }

    [Fact]
    public void V2TransformPolicyDecisionCanCoherentlyAnswer()
    {
        var oracle = Oracle(decisions: new HashSet<AnswerDecision>([AnswerDecision.Answered]),
            safetyOutcomes: [new SafetyOutcomeOracle(SafetyAction.Transform, new HashSet<string>(["PII_REDACTED"]))]);
        var observation = V2Observation(SafetyAction.Transform, "PII_REDACTED", AnswerDecision.Answered,
            ObservedAction.PolicyDecision);
        var result = EvaluationScorer.Score(V2Case(oracle), observation);

        Assert.Equal(CaseVerdict.Pass, result.Verdict);
        Assert.Contains(result.Assertions, item => item.Code == "OBSERVATION_COHERENT");
    }

    [Fact]
    public void V2SafetyOnlySuiteTreatsCitationRecallAsNotApplicable()
    {
        var result = EvaluationScorer.Score(V2Case(Oracle()), V2Observation());
        var report = EvaluationReportBuilder.Build([result]);

        Assert.Null(report.CitationRecall);
        Assert.True(report.QualityGate.Passed);
        Assert.DoesNotContain("CITATION_RECALL_BELOW_THRESHOLD", report.QualityGate.Reasons);
    }

    private static EvaluationCase Case(ExpectedAction action, EvaluationRiskLevel risk = EvaluationRiskLevel.Low,
        IReadOnlyList<ExpectedCitation>? citations = null) => new(1, $"TEST-{action}", Category(action), "demo-beichen",
        "evaluation:test", new HashSet<string>(["all-rnd"]), "问题", "人工期望", "legacy", action, risk, true,
        citations ?? []);

    private static string Category(ExpectedAction action) => action switch
    {
        ExpectedAction.Answer => "factual",
        ExpectedAction.Workflow => "incident",
        ExpectedAction.ClarifyOrRefuse => "no_answer",
        ExpectedAction.ResolveOrEscalate => "conflict",
        ExpectedAction.PolicyDecision => "security",
        ExpectedAction.MemoryPolicy => "memory",
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, null)
    };

    internal static EvaluationObservation Observation(ObservedAction action,
        AnswerDecision decision = AnswerDecision.Answered, SafetyAction safetyAction = SafetyAction.Allow,
        IReadOnlyList<Citation>? citations = null) => new(action, decision, safetyAction,
        safetyAction == SafetyAction.Allow ? "SAFE" : "POLICY_BLOCK", "TEST_TERMINAL", "回答", citations ?? [], []);

    private static EvaluationCase V2Case(EvaluationOracle oracle) => new(2, "V2-TEST", "security", "demo-beichen",
        "evaluation:V2-TEST", new HashSet<string>(["all-rnd"]), "问题", null, null, ExpectedAction.PolicyDecision,
        EvaluationRiskLevel.Critical, true, oracle.RequiredCitations, oracle);

    private static EvaluationOracle Oracle(
        IReadOnlySet<AnswerDecision>? decisions = null,
        IReadOnlyList<SafetyOutcomeOracle>? safetyOutcomes = null,
        IReadOnlyList<ClaimOracle>? requiredClaims = null,
        IReadOnlyList<ClaimOracle>? forbiddenClaims = null,
        IReadOnlyList<ExpectedCitation>? requiredCitations = null,
        IReadOnlyList<ExpectedCitation>? forbiddenCitations = null,
        IReadOnlyList<string>? requiredTrace = null,
        IReadOnlySet<string>? forbiddenTrace = null,
        bool requireNoCitations = false,
        string? fixtureId = null) => new(
        decisions ?? new HashSet<AnswerDecision>([AnswerDecision.Refused]),
        safetyOutcomes ?? [new SafetyOutcomeOracle(SafetyAction.Refuse, new HashSet<string>(["SECRET_REQUEST"]))],
        new HashSet<string>(["SECRET_REQUEST"]), requiredClaims ?? [], forbiddenClaims ?? [],
        requiredCitations ?? [], forbiddenCitations ?? [], requiredTrace ?? [], forbiddenTrace ?? new HashSet<string>(),
        requireNoCitations, fixtureId);

    private static EvaluationObservation V2Observation(SafetyAction safetyAction = SafetyAction.Refuse,
        string safetyCode = "SECRET_REQUEST", AnswerDecision decision = AnswerDecision.Refused,
        ObservedAction action = ObservedAction.PolicyDecision, string answer = "",
        IReadOnlyList<Citation>? citations = null, IReadOnlyList<TraceStep>? trace = null,
        EvaluationFixtureObservation? fixture = null) => new(action, decision, safetyAction, safetyCode,
        "SECRET_REQUEST", answer, citations ?? [], trace ?? [], fixture);

    private static TraceStep Trace(string name) => new(name, "ok", DateTimeOffset.UnixEpoch,
        new Dictionary<string, object?>());
}
