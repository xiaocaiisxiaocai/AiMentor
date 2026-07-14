using AiMentor.Domain;
using AiMentor.Evaluation;
using Xunit;

namespace AiMentor.Tests;

public sealed class EvaluationRunnerAndGateTests
{
    [Fact]
    public async Task RunnerMustPassEachCasesResolvedIdentityToTheTarget()
    {
        var target = new CapturingTarget();
        var evaluationCase = Case(ExpectedAction.ClarifyOrRefuse, EvaluationRiskLevel.Medium,
            new HashSet<string>(["all-employees", "security"]));
        await new EvaluationRunner(target).RunAsync([evaluationCase]);

        var input = Assert.Single(target.Inputs);
        Assert.Equal(evaluationCase.SubjectId, input.Access.SubjectId);
        Assert.True(input.Access.Groups.SetEquals(evaluationCase.Groups));
    }

    [Fact]
    public async Task NullTargetObservationMustBecomeFailedResult()
    {
        var results = await new EvaluationRunner(new NullTarget()).RunAsync([Case(ExpectedAction.Answer)]);

        var result = Assert.Single(results);
        Assert.Equal(CaseVerdict.Fail, result.Verdict);
        Assert.Equal(ObservedAction.Failed, result.ActualAction);
    }

    [Fact]
    public async Task MalformedTargetObservationMustBecomeFailedResult()
    {
        var results = await new EvaluationRunner(new MalformedTarget()).RunAsync([Case(ExpectedAction.Answer)]);

        var result = Assert.Single(results);
        Assert.Equal(CaseVerdict.Fail, result.Verdict);
        Assert.Equal(ObservedAction.Failed, result.ActualAction);
    }

    [Fact]
    public void TwoStateCheaterCannotPassQualityGate()
    {
        // 该目标精确复现旧漏洞：只拒绝两个具名动作，其余全部回答即可假装全量通过。
        var results = Enum.GetValues<ExpectedAction>().Select(action =>
        {
            var observed = action is ExpectedAction.ClarifyOrRefuse or ExpectedAction.MemoryPolicy
                ? ObservedAction.ClarifyOrRefuse
                : ObservedAction.Answer;
            var decision = observed == ObservedAction.Answer ? AnswerDecision.Answered : AnswerDecision.Refused;
            return EvaluationScorer.Score(Case(action),
                EvaluationScorerTests.Observation(observed, decision));
        }).ToArray();

        var report = EvaluationReportBuilder.Build(results);

        Assert.False(report.QualityGate.Passed);
        Assert.Contains(results, result => result.ExpectedAction == ExpectedAction.Workflow
            && result.Assertions.Single(item => item.Name == "action").Status == AssertionStatus.Fail);
        Assert.Contains(results, result => result.ExpectedAction == ExpectedAction.ResolveOrEscalate
            && result.Assertions.Single(item => item.Name == "action").Status == AssertionStatus.Fail);
    }

    [Fact]
    public void AlwaysAnswerAndAlwaysRefuseBothFailActionSemantics()
    {
        var cases = new[] { Case(ExpectedAction.Answer), Case(ExpectedAction.ClarifyOrRefuse) };
        var alwaysAnswer = cases.Select(item => EvaluationScorer.Score(item,
            EvaluationScorerTests.Observation(ObservedAction.Answer))).ToArray();
        var alwaysRefuse = cases.Select(item => EvaluationScorer.Score(item,
            EvaluationScorerTests.Observation(ObservedAction.ClarifyOrRefuse, AnswerDecision.Refused))).ToArray();

        Assert.Contains(alwaysAnswer, result => result.Assertions.Single(item => item.Name == "action").Status == AssertionStatus.Fail);
        Assert.Contains(alwaysRefuse, result => result.Assertions.Single(item => item.Name == "action").Status == AssertionStatus.Fail);
        Assert.False(EvaluationReportBuilder.Build(alwaysAnswer).QualityGate.Passed);
        Assert.False(EvaluationReportBuilder.Build(alwaysRefuse).QualityGate.Passed);
    }

    private static EvaluationCase Case(ExpectedAction action, EvaluationRiskLevel risk = EvaluationRiskLevel.Low,
        IReadOnlySet<string>? groups = null) => new(1, $"TEST-{action}", Category(action), "demo-beichen",
        $"evaluation:{action}", groups ?? new HashSet<string>(["all-rnd"]), "问题", "人工期望", "legacy", action,
        risk, true, action == ExpectedAction.Answer ? [new ExpectedCitation("BK-POL-001", null)] : []);

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

    private sealed class CapturingTarget : IEvaluationTarget
    {
        public List<EvaluationInput> Inputs { get; } = [];

        public Task<EvaluationObservation> ExecuteAsync(EvaluationInput input,
            CancellationToken cancellationToken = default)
        {
            Inputs.Add(input);
            return Task.FromResult(EvaluationScorerTests.Observation(ObservedAction.ClarifyOrRefuse,
                AnswerDecision.InsufficientEvidence));
        }
    }

    private sealed class NullTarget : IEvaluationTarget
    {
        public Task<EvaluationObservation> ExecuteAsync(EvaluationInput input,
            CancellationToken cancellationToken = default) => Task.FromResult<EvaluationObservation>(null!);
    }

    private sealed class MalformedTarget : IEvaluationTarget
    {
        public Task<EvaluationObservation> ExecuteAsync(EvaluationInput input,
            CancellationToken cancellationToken = default) => Task.FromResult(new EvaluationObservation(
            ObservedAction.Answer, AnswerDecision.Answered, SafetyAction.Allow, "SAFE", "SAFE", "回答", null!, null!));
    }
}
