using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Security.Cryptography;
using System.Text;
using AiMentor.Application;
using AiMentor.Domain;

namespace AiMentor.Infrastructure;

/// <summary>仅把稳定阶段、结果码和数值指标写入 Activity，正文与参数默认永久关闭。</summary>
public sealed class OpenTelemetryTraceSink : ITraceSink
{
    public const string SourceName = "AiMentor.TrustedWorkflows";
    private static readonly ActivitySource Source = new(SourceName);
    private static readonly HashSet<string> AllowedDetailKeys = new(StringComparer.Ordinal)
    {
        "code", "policyVersion", "availableTools", "maximumModelIterations", "maximumToolCalls",
        "toolCalls", "sequence", "resultBytes", "acceptedEvidence", "rejectedEvidence", "citationCount",
        "topRerankedScore", "confidence", "status", "provider", "model"
    };

    public Task WriteAsync(string runId, IReadOnlyList<TraceStep> trace,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var activity = Source.StartActivity("aimentor.workflow", ActivityKind.Internal);
        if (activity is null) return Task.CompletedTask;
        activity.SetTag("aimentor.run_id_hash", Hash(runId));
        foreach (var step in trace)
        {
            var tags = new ActivityTagsCollection
            {
                { "aimentor.stage", step.Name },
                { "aimentor.outcome", step.Outcome }
            };
            foreach (var detail in step.Details.Where(item => AllowedDetailKeys.Contains(item.Key)))
            {
                if (detail.Value is string or bool or byte or short or int or long or float or double or decimal)
                    tags[$"aimentor.{detail.Key}"] = detail.Value;
            }
            activity.AddEvent(new ActivityEvent("aimentor.stage", step.Timestamp, tags));
        }
        return Task.CompletedTask;
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..16];
}

/// <summary>只把显式业务边界的完成结果转成指标，避免通用审计事件污染 SLI。</summary>
public sealed class OpenTelemetryWorkflowMetrics : IWorkflowMetrics
{
    public void RecordCompleted(string workflowKind, string outcomeClass, TimeSpan duration,
        IReadOnlyList<TraceStep> trace) =>
        AiMentorTelemetry.RecordWorkflow(workflowKind, outcomeClass, duration, trace);
}

/// <summary>
/// 发布低基数且不含租户、主体、运行标识或正文的核心业务指标。
/// 标签只保留受控分类，未知阶段和结果会折叠为 other，避免输入制造基数爆炸。
/// </summary>
public static class AiMentorTelemetry
{
    public const string MeterName = "AiMentor.TrustedWorkflows";
    public const string WorkflowExecutionsName = "aimentor.workflow.executions";
    public const string WorkflowStageEventsName = "aimentor.workflow.stage.events";
    public const string WorkflowDurationName = "aimentor.workflow.duration";
    public const string TelemetryHeartbeatName = "aimentor.telemetry.heartbeat";
    public const string MemoryRetentionRunsName = "aimentor.memory.retention.runs";
    public const string MemoryRetentionDeletedName = "aimentor.memory.retention.deleted";
    public const string MemoryRetentionDurationName = "aimentor.memory.retention.duration";
    public const string MemoryRetentionLastCompletedAtName = "aimentor.memory.retention.last_completed_at";
    public const string MemoryRetentionMonitoringStartedAtName = "aimentor.memory.retention.monitoring_started_at";

    private static readonly Meter Meter = new(MeterName);
    private static readonly Counter<long> WorkflowExecutions = Meter.CreateCounter<long>(
        WorkflowExecutionsName, "{execution}", "已完成轨迹写入的工作流数量。");
    private static readonly Counter<long> WorkflowStageEvents = Meter.CreateCounter<long>(
        WorkflowStageEventsName, "{event}", "工作流阶段事件数量。");
    private static readonly Histogram<double> WorkflowDuration = Meter.CreateHistogram<double>(
        WorkflowDurationName, "s", "工作流轨迹覆盖的持续时间。");
    // 心跳值必须随时间推进，避免 Collector 重放缓存值时仅凭 Prometheus 样本时间误判为新鲜。
    private static readonly ObservableGauge<long> TelemetryHeartbeat = Meter.CreateObservableGauge(
        TelemetryHeartbeatName, () => DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        description: "自定义业务指标最近一次应用侧采集的 Unix 秒时间。");
    private static readonly Counter<long> MemoryRetentionRuns = Meter.CreateCounter<long>(
        MemoryRetentionRunsName, "{run}", "记忆保留期后台任务执行次数。");
    private static readonly Counter<long> MemoryRetentionDeleted = Meter.CreateCounter<long>(
        MemoryRetentionDeletedName, "{record}", "记忆保留期后台任务删除的密文记录数。");
    private static readonly Histogram<double> MemoryRetentionDuration = Meter.CreateHistogram<double>(
        MemoryRetentionDurationName, "s", "记忆保留期后台任务持续时间。");
    private static long _memoryRetentionLastCompletedAt;
    private static readonly ObservableGauge<long> MemoryRetentionLastCompletedAt = Meter.CreateObservableGauge(
        MemoryRetentionLastCompletedAtName, () => Volatile.Read(ref _memoryRetentionLastCompletedAt),
        description: "数据库共享记忆清理最近成功提交的 Unix 秒时间；零表示当前实例尚未观察到成功。");
    private static long _memoryRetentionMonitoringStartedAt;
    private static readonly ObservableGauge<long> MemoryRetentionMonitoringStartedAt = Meter.CreateObservableGauge(
        MemoryRetentionMonitoringStartedAtName, () => Volatile.Read(ref _memoryRetentionMonitoringStartedAt),
        description: "数据库开始监控记忆清理新鲜度的 Unix 秒时间；零表示共享状态尚未读取成功。");

    private static readonly HashSet<string> StageGroups = new(StringComparer.Ordinal)
    {
        "answer", "citation", "evidence", "identity", "input", "knowledge", "memory", "operational",
        "output", "query", "retrieval", "tool"
    };
    private static readonly HashSet<string> SuccessfulOutcomes = new(StringComparer.Ordinal)
    {
        "allow", "allowed", "answered", "completed", "found", "injected", "mapped", "none", "normalized",
        "ok", "passed", "ranked", "reconciled", "success", "unchanged", "verified"
    };
    private static readonly HashSet<string> RefusedOutcomes = new(StringComparer.Ordinal)
    {
        "conflict", "denied", "filtered", "insufficient", "refuse", "refused", "rejected", "unsafe"
    };
    private static readonly HashSet<string> FailedOutcomes = new(StringComparer.Ordinal)
    {
        "expired", "failed", "outcomeunknown"
    };
    private static readonly HashSet<string> PendingOutcomes = new(StringComparer.Ordinal)
    {
        "awaitingapproval", "awaitingsecondreviewer", "pending", "requiredinputs"
    };

    public static void RecordWorkflow(string workflowKind, string outcomeClass, TimeSpan duration,
        IReadOnlyList<TraceStep> trace)
    {
        var safeKind = workflowKind == "trusted_question" ? workflowKind : "other";
        var safeOutcome = outcomeClass is "success" or "failure" or "refused" or "cancelled"
            ? outcomeClass
            : "other";
        foreach (var step in trace)
        {
            var tags = new TagList
            {
                { "aimentor.workflow_kind", safeKind },
                { "aimentor.stage_group", ClassifyStage(step.Name) },
                { "aimentor.outcome_class", ClassifyOutcome(step.Outcome) }
            };
            WorkflowStageEvents.Add(1, tags);
        }

        var terminalTags = new TagList
        {
            { "aimentor.workflow_kind", safeKind },
            { "aimentor.outcome_class", safeOutcome }
        };
        WorkflowExecutions.Add(1, terminalTags);
        WorkflowDuration.Record(Math.Max(0, duration.TotalSeconds), terminalTags);
    }

    public static void RecordMemoryRetention(string result, int proposalsDeleted, int memoriesDeleted,
        TimeSpan duration, DateTimeOffset? lastCompletedAt = null, DateTimeOffset? monitoringStartedAt = null)
    {
        var safeResult = result is "completed" or "lock_not_acquired" or "failed" ? result : "other";
        var runTags = new TagList { { "aimentor.result", safeResult } };
        MemoryRetentionRuns.Add(1, runTags);
        MemoryRetentionDuration.Record(Math.Max(0, duration.TotalSeconds), runTags);
        if (lastCompletedAt is { } completedAt)
            Interlocked.Exchange(ref _memoryRetentionLastCompletedAt, completedAt.ToUnixTimeSeconds());
        if (monitoringStartedAt is { } startedAt)
            Interlocked.Exchange(ref _memoryRetentionMonitoringStartedAt, startedAt.ToUnixTimeSeconds());
        if (proposalsDeleted > 0)
            MemoryRetentionDeleted.Add(proposalsDeleted,
                new TagList { { "aimentor.record_type", "proposal" } });
        if (memoriesDeleted > 0)
            MemoryRetentionDeleted.Add(memoriesDeleted,
                new TagList { { "aimentor.record_type", "memory" } });
    }

    private static string ClassifyStage(string value)
    {
        var separator = value.IndexOf('.', StringComparison.Ordinal);
        var group = (separator < 0 ? value : value[..separator]).Trim().ToLowerInvariant();
        return StageGroups.Contains(group) ? group : "other";
    }

    private static string ClassifyOutcome(string value)
    {
        var normalized = value.Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal).Trim().ToLowerInvariant();
        if (SuccessfulOutcomes.Contains(normalized)) return "success";
        if (RefusedOutcomes.Contains(normalized)) return "refused";
        if (FailedOutcomes.Contains(normalized)) return "failure";
        if (PendingOutcomes.Contains(normalized)) return "pending";
        return "other";
    }
}

/// <summary>同时写入多个轨迹目标；单个目标失败会使调用失败，避免把审计丢失误报为成功。</summary>
public sealed class CompositeTraceSink(params ITraceSink[] sinks) : ITraceSink
{
    public async Task WriteAsync(string runId, IReadOnlyList<TraceStep> trace,
        CancellationToken cancellationToken = default)
    {
        foreach (var sink in sinks) await sink.WriteAsync(runId, trace, cancellationToken);
    }
}
