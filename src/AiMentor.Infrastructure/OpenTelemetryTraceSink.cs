using System.Diagnostics;
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

/// <summary>同时写入多个轨迹目标；单个目标失败会使调用失败，避免把审计丢失误报为成功。</summary>
public sealed class CompositeTraceSink(params ITraceSink[] sinks) : ITraceSink
{
    public async Task WriteAsync(string runId, IReadOnlyList<TraceStep> trace,
        CancellationToken cancellationToken = default)
    {
        foreach (var sink in sinks) await sink.WriteAsync(runId, trace, cancellationToken);
    }
}
