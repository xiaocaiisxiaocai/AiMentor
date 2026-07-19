using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Text;
using AiMentor.Domain;
using AiMentor.Infrastructure;
using Microsoft.Extensions.AI;
using Xunit;

namespace AiMentor.Tests;

public sealed class ProductionAiProviderTests
{
    [Fact]
    public void RemoteProviderRequiresHttpsAndCredentials()
    {
        var exception = Assert.Throws<AiProviderConfigurationException>(() => AiProviderFactory.CreateChat(new()
        {
            Provider = AiProviderKind.OpenAI,
            Endpoint = "http://api.example",
            ApiKey = "secret",
            Model = "model"
        }));
        Assert.Equal("AI_ENDPOINT_HTTPS_REQUIRED", exception.Code);

        var missing = Assert.Throws<AiProviderConfigurationException>(() => AiProviderFactory.CreateChat(new()
        {
            Provider = AiProviderKind.OpenAI,
            Endpoint = "https://api.example",
            Model = "model"
        }));
        Assert.Equal("AI_PROVIDER_CREDENTIALS_MISSING", missing.Code);
    }

    [Fact]
    public void InsecureProtocolAcceptanceMustBeExplicitAndLoopbackOnly()
    {
        var missingOptIn = Assert.Throws<AiProviderConfigurationException>(() => AiProviderFactory.CreateChat(new()
        {
            Provider = AiProviderKind.OpenAI,
            Endpoint = "http://127.0.0.1:5123",
            ApiKey = "secret",
            Model = "model"
        }));
        Assert.Equal("AI_ENDPOINT_HTTPS_REQUIRED", missingOptIn.Code);

        var nonLoopback = Assert.Throws<AiProviderConfigurationException>(() => AiProviderFactory.CreateChat(new()
        {
            Provider = AiProviderKind.OpenAI,
            Endpoint = "http://api.example",
            ApiKey = "secret",
            Model = "model",
            AllowInsecureLoopback = true
        }));
        Assert.Equal("AI_ENDPOINT_HTTPS_REQUIRED", nonLoopback.Code);

        using var accepted = AiProviderFactory.CreateChat(new ModelProviderOptions
        {
            Provider = AiProviderKind.OpenAI,
            Endpoint = "http://127.0.0.1:5123",
            ApiKey = "secret",
            Model = "model",
            AllowInsecureLoopback = true
        }, new StaticHandler("{\"choices\":[{\"message\":{\"content\":\"ok\"}}]}"));
        Assert.NotNull(accepted);
    }

    [Fact]
    public async Task ChatRetriesBounded429AndDoesNotExposeApiKeyInBody()
    {
        var handler = new SequenceHandler(HttpStatusCode.TooManyRequests, HttpStatusCode.InternalServerError,
            HttpStatusCode.OK);
        using var client = AiProviderFactory.CreateChat(RemoteOptions(maximumRetries: 2), handler);

        var response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hello")]);

        Assert.Equal("grounded", response.Text);
        Assert.Equal(3, handler.Calls);
        Assert.All(handler.Bodies, body => Assert.DoesNotContain("test-api-key", body, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ChatStopsAfterRetryBudgetWithoutSandboxFallback()
    {
        var handler = new SequenceHandler(HttpStatusCode.ServiceUnavailable, HttpStatusCode.ServiceUnavailable);
        using var client = AiProviderFactory.CreateChat(RemoteOptions(maximumRetries: 1), handler);

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.GetResponseAsync([new ChatMessage(ChatRole.User, "hello")]));
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task CancellationPropagatesToRemoteRequest()
    {
        var handler = new BlockingHandler();
        using var client = AiProviderFactory.CreateChat(RemoteOptions(), handler);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.GetResponseAsync([new ChatMessage(ChatRole.User, "hello")], cancellationToken: cancellation.Token));
    }

    [Fact]
    public async Task EmbeddingResponseMustMatchConfiguredIndexDimensions()
    {
        var handler = new StaticHandler("{\"data\":[{\"embedding\":[0.1,0.2]}]}");
        var options = new EmbeddingProviderOptions
        {
            Provider = AiProviderKind.OpenAI,
            Endpoint = "https://api.example",
            ApiKey = "key",
            Model = "embed",
            Dimensions = 3,
            IndexVersion = "knowledge-v2"
        };
        var generator = AiProviderFactory.CreateEmbedding(options, handler);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => generator.GenerateAsync("hello"));
        Assert.Equal("AI_EMBEDDING_DIMENSIONS_MISMATCH", exception.Message);
    }

    [Fact]
    public async Task ActivityTraceUsesAllowlistAndHashesRunId()
    {
        Activity? captured = null;
        var measurements = new List<(string Name, string Tags)>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == OpenTelemetryTraceSink.SourceName,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity => captured = activity
        };
        ActivitySource.AddActivityListener(listener);
        using var meterListener = new MeterListener
        {
            InstrumentPublished = (instrument, activeListener) =>
            {
                if (instrument.Meter.Name == AiMentorTelemetry.MeterName)
                    activeListener.EnableMeasurementEvents(instrument);
            }
        };
        meterListener.SetMeasurementEventCallback<long>((instrument, _, tags, _) =>
            measurements.Add((instrument.Name, SerializeMetricTags(tags))));
        meterListener.SetMeasurementEventCallback<int>((instrument, _, tags, _) =>
            measurements.Add((instrument.Name, SerializeMetricTags(tags))));
        meterListener.SetMeasurementEventCallback<double>((instrument, _, tags, _) =>
            measurements.Add((instrument.Name, SerializeMetricTags(tags))));
        meterListener.Start();
        var sink = new OpenTelemetryTraceSink();
        TraceStep[] trace = [new TraceStep("stage", "ok", DateTimeOffset.UtcNow,
            new Dictionary<string, object?>
            {
                ["code"] = "SAFE_CODE", ["prompt"] = "private prompt", ["tool"] = "sensitive arguments",
                ["token"] = "secret-token"
            })];
        await sink.WriteAsync("run-secret", trace);
        Assert.DoesNotContain(measurements, item => item.Name == AiMentorTelemetry.WorkflowExecutionsName);
        new OpenTelemetryWorkflowMetrics().RecordCompleted("trusted_question", "success",
            TimeSpan.FromSeconds(1), trace);
        AiMentorTelemetry.RecordMemoryRetention("completed", 2, 3, TimeSpan.FromSeconds(1));
        meterListener.RecordObservableInstruments();

        Assert.NotNull(captured);
        var serialized = string.Join('|', captured!.Tags.Select(x => $"{x.Key}={x.Value}")) + string.Join('|',
            captured.Events.SelectMany(x => x.Tags).Select(x => $"{x.Key}={x.Value}"));
        Assert.Contains("SAFE_CODE", serialized);
        Assert.DoesNotContain("run-secret", serialized);
        Assert.DoesNotContain("private prompt", serialized);
        Assert.DoesNotContain("sensitive arguments", serialized);
        Assert.DoesNotContain("secret-token", serialized);

        Assert.Contains(measurements, item => item.Name == AiMentorTelemetry.WorkflowExecutionsName);
        Assert.Contains(measurements, item => item.Name == AiMentorTelemetry.WorkflowStageEventsName);
        Assert.Contains(measurements, item => item.Name == AiMentorTelemetry.WorkflowDurationName);
        Assert.Contains(measurements, item => item.Name == AiMentorTelemetry.TelemetryHeartbeatName);
        Assert.Contains(measurements, item => item.Name == AiMentorTelemetry.MemoryRetentionRunsName);
        Assert.Contains(measurements, item => item.Name == AiMentorTelemetry.MemoryRetentionDeletedName);
        Assert.Contains(measurements, item => item.Name == AiMentorTelemetry.MemoryRetentionDurationName);
        Assert.Contains(measurements, item => item.Name == AiMentorTelemetry.MemoryRetentionLastCompletedAtName);
        Assert.Contains(measurements, item => item.Name == AiMentorTelemetry.MemoryRetentionMonitoringStartedAtName);
        var metricTags = string.Join('|', measurements.Select(item => item.Tags));
        Assert.DoesNotContain("run-secret", metricTags);
        Assert.DoesNotContain("private prompt", metricTags);
        Assert.DoesNotContain("sensitive arguments", metricTags);
        Assert.DoesNotContain("secret-token", metricTags);
    }

    private static string SerializeMetricTags(ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var values = new string[tags.Length];
        for (var index = 0; index < tags.Length; index++)
            values[index] = $"{tags[index].Key}={tags[index].Value}";
        return string.Join(',', values);
    }

    private static ModelProviderOptions RemoteOptions(int maximumRetries = 2) => new()
    {
        Provider = AiProviderKind.OpenAI,
        Endpoint = "https://api.example",
        ApiKey = "test-api-key",
        Model = "model",
        MaximumRetries = maximumRetries
    };

    private sealed class SequenceHandler(params HttpStatusCode[] statuses) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public List<string> Bodies { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            var status = statuses[Math.Min(Calls++, statuses.Length - 1)];
            return new HttpResponseMessage(status)
            {
                Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"grounded\"}}]}", Encoding.UTF8,
                    "application/json")
            };
        }
    }

    private sealed class StaticHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
    }

    private sealed class BlockingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new UnreachableException();
        }
    }
}
