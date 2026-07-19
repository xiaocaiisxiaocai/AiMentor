using AiMentor.Infrastructure;
using Xunit;

namespace AiMentor.Tests;

public sealed class ObservabilityArtifactTests
{
    [Fact]
    public void PrometheusRulesFailClosedAndDocumentMissingTelemetryAsUnhealthy()
    {
        var root = FindRoot();
        var chartRoot = Path.Combine(root, "deploy", "helm", "aimentor");
        var values = File.ReadAllText(Path.Combine(chartRoot, "values.yaml"));
        var rules = File.ReadAllText(Path.Combine(chartRoot, "templates", "prometheusrule.yaml"));
        var runbook = File.ReadAllText(Path.Combine(root, "docs", "operations", "observability.md"));
        var program = File.ReadAllText(Path.Combine(root, "src", "AiMentor.Api", "Program.cs"));

        var normalizedValues = values.ReplaceLineEndings("\n");
        Assert.Contains("prometheusRule:\n    enabled: false", normalizedValues,
            StringComparison.Ordinal);
        Assert.Contains("team: \"\"", normalizedValues, StringComparison.Ordinal);
        Assert.Contains("targetJobRegex: \"\"", normalizedValues, StringComparison.Ordinal);
        Assert.Contains("collectorJobRegex: \"\"", normalizedValues, StringComparison.Ordinal);
        Assert.Contains("if .Values.monitoring.prometheusRule.enabled", rules, StringComparison.Ordinal);
        Assert.Contains("required \"monitoring.prometheusRule.team", rules, StringComparison.Ordinal);
        Assert.Contains("required \"monitoring.prometheusRule.runbook", rules, StringComparison.Ordinal);
        Assert.Contains("required \"monitoring.prometheusRule.targetJobRegex", rules, StringComparison.Ordinal);
        Assert.Contains("required \"monitoring.prometheusRule.collectorJobRegex", rules, StringComparison.Ordinal);
        Assert.Contains("kind: PrometheusRule", rules, StringComparison.Ordinal);

        Assert.Contains("AiMentorWorkflowFailureRatioHigh", rules, StringComparison.Ordinal);
        Assert.Contains("aimentor_workflow_executions_total{aimentor_workflow_kind=\"trusted_question\",aimentor_outcome_class=\"failure\"}", rules,
            StringComparison.Ordinal);
        Assert.Contains("aimentor_workflow_kind=\"trusted_question\"", rules, StringComparison.Ordinal);
        Assert.Contains("aimentor_outcome_class=~\"success|failure\"", rules, StringComparison.Ordinal);
        Assert.Contains("AiMentorWorkflowP95DurationHigh", rules, StringComparison.Ordinal);
        Assert.Contains("aimentor_workflow_duration_seconds_bucket", rules, StringComparison.Ordinal);
        Assert.Contains("AiMentorMemoryRetentionFailed", rules, StringComparison.Ordinal);
        Assert.Contains("aimentor_memory_retention_runs_total{aimentor_result=\"failed\"}", rules,
            StringComparison.Ordinal);
        Assert.Contains("AiMentorMemoryRetentionNoCompleted", rules, StringComparison.Ordinal);
        Assert.Contains("aimentor_memory_retention_last_completed_at", rules, StringComparison.Ordinal);
        Assert.Contains("aimentor_memory_retention_monitoring_started_at", rules, StringComparison.Ordinal);
        Assert.Contains("absent(aimentor_memory_retention_last_completed_at", rules, StringComparison.Ordinal);
        Assert.Contains("AiMentorBusinessTelemetryMissing", rules, StringComparison.Ordinal);
        Assert.Contains("absent(aimentor_telemetry_heartbeat{job=~", rules,
            StringComparison.Ordinal);
        Assert.Contains("max by (job, instance) (aimentor_telemetry_heartbeat", rules,
            StringComparison.Ordinal);
        Assert.Contains("max_over_time(aimentor_telemetry_heartbeat", rules, StringComparison.Ordinal);
        Assert.Contains("telemetryHeartbeatMaxAgeSeconds", rules,
            StringComparison.Ordinal);
        Assert.Contains("AiMentorTelemetryTargetMissing", rules, StringComparison.Ordinal);
        Assert.Contains("up{job=~", rules, StringComparison.Ordinal);
        Assert.Contains("== 0", rules, StringComparison.Ordinal);
        Assert.Contains("absent(up{job=~", rules, StringComparison.Ordinal);
        Assert.Contains("AiMentorRuleGroupEvaluationMissing", rules, StringComparison.Ordinal);
        Assert.Contains("prometheus_rule_group_last_evaluation_timestamp_seconds", rules,
            StringComparison.Ordinal);
        Assert.Contains("severity:", rules, StringComparison.Ordinal);
        Assert.Contains("team:", rules, StringComparison.Ordinal);
        Assert.Contains("runbook:", rules, StringComparison.Ordinal);
        Assert.DoesNotContain("tenant", rules, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("run_id", rules, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ExplicitBucketHistogramConfiguration", program, StringComparison.Ordinal);
        Assert.Contains("0.1, 0.25, 0.5, 1, 2, 5, 10, 12, 15, 18, 20, 30, 60, 120", program,
            StringComparison.Ordinal);

        Assert.Contains("没有数据、规则未评估或采集目标消失均为“未知/不健康”", runbook,
            StringComparison.Ordinal);
        Assert.Contains("不得成为指标标签", runbook, StringComparison.Ordinal);
        Assert.Contains("工作流成功率", runbook, StringComparison.Ordinal);
        Assert.Contains("工作流 P95 持续时间", runbook, StringComparison.Ordinal);
        Assert.Contains("记忆清理成功", runbook, StringComparison.Ordinal);
        Assert.Contains("ObservableGauge", runbook, StringComparison.Ordinal);
        Assert.Contains("PrometheusRule 自身无法在完全未加载时可靠地自报故障", runbook,
            StringComparison.Ordinal);
    }

    private static string FindRoot()
    {
        var knowledgeRoot = WorkspacePathLocator.FindKnowledgeRoot();
        return Directory.GetParent(Directory.GetParent(knowledgeRoot)!.FullName)!.FullName;
    }
}
