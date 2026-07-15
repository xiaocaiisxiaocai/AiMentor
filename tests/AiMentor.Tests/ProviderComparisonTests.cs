using System.Text.Json;
using AiMentor.Evaluation;
using Xunit;

namespace AiMentor.Tests;

public sealed class ProviderComparisonTests
{
    private static readonly EvaluationCase[] Cases =
    [
        Case("C-1", EvaluationRiskLevel.Critical),
        Case("L-1", EvaluationRiskLevel.Low)
    ];

    [Fact]
    public async Task ComparisonPairsSameLockedSuiteAndLeavesUnknownUsageAndCostNull()
    {
        var report = await ProviderComparisonRunner.RunAsync("ABC123", Cases, Descriptor("Deterministic"),
            (item, _) => Task.FromResult(new ProviderCaseExecution(CaseVerdict.Pass)),
            Descriptor("OpenAI"),
            (item, _) => Task.FromResult(new ProviderCaseExecution(CaseVerdict.Pass)));

        Assert.Equal("ABC123", report.SuiteSha256);
        Assert.Equal(report.Baseline.Cases.Select(item => item.CaseId),
            report.Candidate.Cases.Select(item => item.CaseId));
        Assert.Null(report.Candidate.Aggregate.InputTokens);
        Assert.Null(report.Candidate.Aggregate.Cost);
        Assert.True(report.Passed);
    }

    [Fact]
    public async Task ProviderFailureBecomesNotReadyAndCriticalBlockerWithoutFallback()
    {
        var report = await ProviderComparisonRunner.RunAsync("ABC123", Cases, Descriptor("Deterministic"),
            (_, _) => Task.FromResult(new ProviderCaseExecution(CaseVerdict.Pass)), Descriptor("OpenAI"),
            (_, _) => throw new HttpRequestException("secret remote body"));

        Assert.Equal(2, report.Candidate.Aggregate.NotReady);
        Assert.Equal(2, report.Candidate.Aggregate.Errors);
        Assert.Contains("CANDIDATE_CRITICAL_CASE_BLOCKED", report.BlockingReasons);
        Assert.False(report.Passed);
    }

    [Fact]
    public async Task ReportContainsOnlySafeMetadataAndNeverSerializesEvaluationContentOrCredentials()
    {
        var sensitiveCase = Cases[0] with { Input = "private-prompt-api-key-secret" };
        var report = await ProviderComparisonRunner.RunAsync("ABC123", [sensitiveCase], Descriptor("Deterministic"),
            (_, _) => Task.FromResult(new ProviderCaseExecution(CaseVerdict.Pass)), Descriptor("OpenAI"),
            (_, _) => throw new InvalidOperationException("private-evidence-api-key-secret"));

        var json = JsonSerializer.Serialize(report);
        Assert.DoesNotContain("private-prompt", json, StringComparison.Ordinal);
        Assert.DoesNotContain("private-evidence", json, StringComparison.Ordinal);
        Assert.DoesNotContain("api-key-secret", json, StringComparison.Ordinal);
        Assert.Contains("PROVIDER_INVALIDOPERATIONEXCEPTION", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CostRequiresExplicitVersionedPriceAndCompleteUsage()
    {
        var report = await ProviderComparisonRunner.RunAsync("ABC123", Cases, Descriptor("Deterministic"),
            (_, _) => Task.FromResult(new ProviderCaseExecution(CaseVerdict.Pass,
                new ProviderTokenUsage(10, 5))), Descriptor("OpenAI"),
            (_, _) => Task.FromResult(new ProviderCaseExecution(CaseVerdict.Pass,
                new ProviderTokenUsage(1_000_000, 2_000_000))),
            new VersionedTokenPrice("pricing-2026-07-15", 1m, 2m));

        Assert.Equal(10m, report.Candidate.Aggregate.Cost);
        Assert.Null(report.Baseline.Aggregate.Cost);
        Assert.Null(report.Delta.Cost);
    }

    private static ProviderComparisonDescriptor Descriptor(string provider) =>
        new(provider, "chat-model", provider, "embedding-model", "knowledge-v2", provider, "reranker-model");

    private static EvaluationCase Case(string id, EvaluationRiskLevel risk) => new(2, id, "comparison", "tenant",
        "subject", new HashSet<string>(["readers"]), "sensitive prompt", null, null, ExpectedAction.Answer, risk,
        true, []);
}
