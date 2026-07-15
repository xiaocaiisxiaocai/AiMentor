using AiMentor.Domain;
using AiMentor.Evaluation;
using Xunit;

namespace AiMentor.Tests;

public sealed class OperationalEvaluationFixtureRegistryTests
{
    [Theory]
    [InlineData(BuiltInOperationalEvaluationFixtures.CrossUserMemory, "MEMORY_CROSS_USER_ISOLATED")]
    [InlineData(BuiltInOperationalEvaluationFixtures.DeletedMemory, "MEMORY_DELETE_VISIBLE_IMMEDIATELY")]
    [InlineData(BuiltInOperationalEvaluationFixtures.ToolArguments, "TOOL_ARGUMENTS_EXACTLY_CAPTURED")]
    [InlineData(BuiltInOperationalEvaluationFixtures.ToolScopeMismatch, "TOOL_APPROVAL_SCOPE_MISMATCH")]
    [InlineData(BuiltInOperationalEvaluationFixtures.ToolReplay, "TOOL_APPROVAL_ALREADY_CONSUMED")]
    [InlineData(BuiltInOperationalEvaluationFixtures.ToolExpired, "TOOL_APPROVAL_EXPIRED")]
    public async Task BuiltInScenarioMustBeVerifiedByRealRuntimeFacts(string fixtureId, string expectedCode)
    {
        var definition = Assert.Single(BuiltInOperationalEvaluationFixtures.Create(), item => item.FixtureId == fixtureId);
        var observation = await Registry().ExecuteAsync(Input(definition), new SelfReportingTarget());

        Assert.Equal(EvaluationFixtureStatus.Ready, observation.Fixture?.Status);
        Assert.Equal(expectedCode, observation.Fixture?.Code);
        Assert.Equal(expectedCode, observation.TerminalCode);
    }

    [Fact]
    public async Task PrincipalMismatchMustFailClosedWithoutTrustingTargetFixture()
    {
        var definition = BuiltInOperationalEvaluationFixtures.Create()[0];
        var input = Input(definition, AccessContext.Create(definition.TenantId, "attacker", definition.Groups));

        var observation = await Registry().ExecuteAsync(input, new SelfReportingTarget());

        Assert.Equal(EvaluationFixtureStatus.VerificationFailed, observation.Fixture?.Status);
        Assert.Equal("OPERATIONAL_INPUT_MISMATCH", observation.Fixture?.Code);
    }

    [Fact]
    public async Task UnknownFixtureMustFailClosed()
    {
        var definition = BuiltInOperationalEvaluationFixtures.Create()[0] with { FixtureId = "unknown" };
        var observation = await Registry().ExecuteAsync(Input(definition), new SelfReportingTarget());

        Assert.Equal(EvaluationFixtureStatus.VerificationFailed, observation.Fixture?.Status);
        Assert.Equal("FIXTURE_NOT_REGISTERED", observation.Fixture?.Code);
    }

    private static OperationalEvaluationFixtureRegistry Registry() =>
        new(BuiltInOperationalEvaluationFixtures.Create());

    private static EvaluationInput Input(OperationalFixtureDefinition definition, AccessContext? access = null)
    {
        var oracle = new EvaluationOracle(new HashSet<AnswerDecision>([AnswerDecision.Answered]),
            [new SafetyOutcomeOracle(SafetyAction.Allow, new HashSet<string>(["SAFE"]))],
            new HashSet<string>(["fixture"]), [], [], [], [], ["operational.fixture"],
            new HashSet<string>(), true, definition.FixtureId);
        var evaluationCase = new EvaluationCase(2, definition.CaseId, "security", definition.TenantId,
            definition.SubjectId, definition.Groups, definition.Input, null, null, ExpectedAction.Workflow,
            EvaluationRiskLevel.Critical, true, [], oracle);
        return new EvaluationInput(evaluationCase, access ?? AccessContext.Create(definition.TenantId,
            definition.SubjectId, definition.Groups));
    }

    private sealed class SelfReportingTarget : IEvaluationTarget
    {
        public Task<EvaluationObservation> ExecuteAsync(EvaluationInput input,
            CancellationToken cancellationToken = default) => Task.FromResult(new EvaluationObservation(
            ObservedAction.Answer, AnswerDecision.Answered, SafetyAction.Allow, "SAFE", "TARGET_SELF_REPORTED",
            "目标自报成功。", [], [], new EvaluationFixtureObservation(input.Case.Oracle!.FixtureId!,
                EvaluationFixtureStatus.Ready, [], "TARGET_SELF_REPORTED")));
    }
}
