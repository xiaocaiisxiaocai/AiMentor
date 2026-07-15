using System.Diagnostics;

namespace AiMentor.Evaluation;

public sealed record ProviderComparisonDescriptor(
    string ChatProvider,
    string ChatModel,
    string EmbeddingProvider,
    string EmbeddingModel,
    string EmbeddingIndexVersion,
    string RerankerProvider,
    string RerankerModel);

public sealed record ProviderTokenUsage(long InputTokens, long OutputTokens);

public sealed record VersionedTokenPrice(
    string Version,
    decimal InputPerMillionTokens,
    decimal OutputPerMillionTokens);

public sealed record ProviderCaseMeasurement(
    string CaseId,
    CaseVerdict Verdict,
    double LatencyMilliseconds,
    ProviderTokenUsage? Usage,
    decimal? Cost,
    string? ErrorCode);

public sealed record ProviderAggregateMeasurement(
    int Total,
    int Passed,
    int Failed,
    int NotReady,
    int Errors,
    double P50LatencyMilliseconds,
    double P95LatencyMilliseconds,
    long? InputTokens,
    long? OutputTokens,
    decimal? Cost);

public sealed record ProviderComparisonDelta(
    int Passed,
    int Failed,
    int NotReady,
    int Errors,
    double P50LatencyMilliseconds,
    double P95LatencyMilliseconds,
    decimal? Cost);

public sealed record ProviderComparisonRun(
    ProviderComparisonDescriptor Provider,
    ProviderAggregateMeasurement Aggregate,
    IReadOnlyList<ProviderCaseMeasurement> Cases);

public sealed record ProviderComparisonReport(
    int SchemaVersion,
    DateTimeOffset GeneratedAt,
    string SuiteSha256,
    ProviderComparisonRun Baseline,
    ProviderComparisonRun Candidate,
    ProviderComparisonDelta Delta,
    bool Passed,
    IReadOnlyList<string> BlockingReasons);

public sealed record ProviderCaseExecution(
    CaseVerdict Verdict,
    ProviderTokenUsage? Usage = null,
    string? ErrorCode = null);

/// <summary>在同一锁定题集上执行配对测量；输出模型故意不包含问题、证据、回答或凭据。</summary>
public static class ProviderComparisonRunner
{
    public static async Task<ProviderComparisonReport> RunAsync(
        string suiteSha256,
        IReadOnlyList<EvaluationCase> cases,
        ProviderComparisonDescriptor baselineDescriptor,
        Func<EvaluationCase, CancellationToken, Task<ProviderCaseExecution>> baseline,
        ProviderComparisonDescriptor candidateDescriptor,
        Func<EvaluationCase, CancellationToken, Task<ProviderCaseExecution>> candidate,
        VersionedTokenPrice? candidatePrice = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(suiteSha256))
            throw new ArgumentException("套件哈希不能为空。", nameof(suiteSha256));
        if (cases.Count == 0) throw new ArgumentException("对比题集不能为空。", nameof(cases));
        if (cases.Select(item => item.CaseId).Distinct(StringComparer.Ordinal).Count() != cases.Count)
            throw new ArgumentException("对比题集 CaseId 必须唯一。", nameof(cases));
        ValidatePrice(candidatePrice);

        var baselineCases = await MeasureAsync(cases, baseline, null, cancellationToken);
        var candidateCases = await MeasureAsync(cases, candidate, candidatePrice, cancellationToken);
        // CaseId 配对是报告可信边界；任何漏跑或乱序都不能静默生成不可比较的 delta。
        if (!baselineCases.Select(item => item.CaseId).SequenceEqual(candidateCases.Select(item => item.CaseId),
                StringComparer.Ordinal))
            throw new InvalidOperationException("PROVIDER_COMPARISON_PAIRING_MISMATCH");

        var baselineRun = new ProviderComparisonRun(baselineDescriptor, Aggregate(baselineCases), baselineCases);
        var candidateRun = new ProviderComparisonRun(candidateDescriptor, Aggregate(candidateCases), candidateCases);
        var blockers = new List<string>();
        if (candidateCases.Any(item => item.ErrorCode is not null)) blockers.Add("CANDIDATE_PROVIDER_ERROR");
        if (candidateCases.Any(item => item.Verdict == CaseVerdict.NotReady)) blockers.Add("CANDIDATE_NOT_READY");
        if (cases.Zip(candidateCases).Any(pair => pair.First.RiskLevel == EvaluationRiskLevel.Critical
                                                 && pair.Second.Verdict != CaseVerdict.Pass))
            blockers.Add("CANDIDATE_CRITICAL_CASE_BLOCKED");
        var delta = new ProviderComparisonDelta(
            candidateRun.Aggregate.Passed - baselineRun.Aggregate.Passed,
            candidateRun.Aggregate.Failed - baselineRun.Aggregate.Failed,
            candidateRun.Aggregate.NotReady - baselineRun.Aggregate.NotReady,
            candidateRun.Aggregate.Errors - baselineRun.Aggregate.Errors,
            candidateRun.Aggregate.P50LatencyMilliseconds - baselineRun.Aggregate.P50LatencyMilliseconds,
            candidateRun.Aggregate.P95LatencyMilliseconds - baselineRun.Aggregate.P95LatencyMilliseconds,
            candidateRun.Aggregate.Cost is not null && baselineRun.Aggregate.Cost is not null
                ? candidateRun.Aggregate.Cost - baselineRun.Aggregate.Cost : null);
        return new ProviderComparisonReport(1, DateTimeOffset.UtcNow, suiteSha256, baselineRun, candidateRun, delta,
            blockers.Count == 0, blockers);
    }

    private static async Task<IReadOnlyList<ProviderCaseMeasurement>> MeasureAsync(
        IReadOnlyList<EvaluationCase> cases,
        Func<EvaluationCase, CancellationToken, Task<ProviderCaseExecution>> execute,
        VersionedTokenPrice? price,
        CancellationToken cancellationToken)
    {
        var results = new List<ProviderCaseMeasurement>(cases.Count);
        foreach (var evaluationCase in cases)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var stopwatch = Stopwatch.StartNew();
            ProviderCaseExecution execution;
            try
            {
                execution = await execute(evaluationCase, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                // 远端异常不能回退或冒充普通 Fail；NotReady 加稳定异常类型供门禁阻断。
                execution = new ProviderCaseExecution(CaseVerdict.NotReady, null,
                    $"PROVIDER_{exception.GetType().Name.ToUpperInvariant()}");
            }
            stopwatch.Stop();
            results.Add(new ProviderCaseMeasurement(evaluationCase.CaseId, execution.Verdict,
                stopwatch.Elapsed.TotalMilliseconds, execution.Usage, CalculateCost(execution.Usage, price),
                execution.ErrorCode));
        }
        return results;
    }

    private static ProviderAggregateMeasurement Aggregate(IReadOnlyList<ProviderCaseMeasurement> cases)
    {
        var latencies = cases.Select(item => item.LatencyMilliseconds).Order().ToArray();
        var completeUsage = cases.All(item => item.Usage is not null);
        var completeCost = cases.All(item => item.Cost is not null);
        return new ProviderAggregateMeasurement(cases.Count,
            cases.Count(item => item.Verdict == CaseVerdict.Pass),
            cases.Count(item => item.Verdict == CaseVerdict.Fail),
            cases.Count(item => item.Verdict == CaseVerdict.NotReady),
            cases.Count(item => item.ErrorCode is not null),
            Percentile(latencies, 0.50), Percentile(latencies, 0.95),
            completeUsage ? cases.Sum(item => item.Usage!.InputTokens) : null,
            completeUsage ? cases.Sum(item => item.Usage!.OutputTokens) : null,
            completeCost ? cases.Sum(item => item.Cost!.Value) : null);
    }

    private static double Percentile(double[] sorted, double percentile)
    {
        var index = (int)Math.Ceiling(percentile * sorted.Length) - 1;
        return sorted[Math.Clamp(index, 0, sorted.Length - 1)];
    }

    private static decimal? CalculateCost(ProviderTokenUsage? usage, VersionedTokenPrice? price) =>
        usage is null || price is null ? null :
        usage.InputTokens * price.InputPerMillionTokens / 1_000_000m
        + usage.OutputTokens * price.OutputPerMillionTokens / 1_000_000m;

    private static void ValidatePrice(VersionedTokenPrice? price)
    {
        if (price is null) return;
        if (string.IsNullOrWhiteSpace(price.Version) || price.InputPerMillionTokens < 0
                                                    || price.OutputPerMillionTokens < 0)
            throw new ArgumentException("价格必须包含显式版本且不得为负数。", nameof(price));
    }
}
