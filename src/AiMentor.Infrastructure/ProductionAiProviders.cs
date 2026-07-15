using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AiMentor.Application;
using AiMentor.Domain;
using Microsoft.Extensions.AI;

namespace AiMentor.Infrastructure;

public enum AiProviderKind { Deterministic, Lexical, OpenAI, AzureOpenAI, HttpSemantic }

public record ModelProviderOptions
{
    public AiProviderKind Provider { get; init; } = AiProviderKind.Deterministic;
    public string? Endpoint { get; init; }
    public string? ApiKey { get; init; }
    public string? Model { get; init; }
    public int TimeoutSeconds { get; init; } = 30;
    public int MaximumRetries { get; init; } = 2;
}

public sealed record EmbeddingProviderOptions : ModelProviderOptions
{
    public int Dimensions { get; init; } = 256;
    public string IndexVersion { get; init; } = "aimentor-knowledge-v1";
}

public sealed record RerankerProviderOptions : ModelProviderOptions;

/// <summary>描述实际创建的运行时实现，SystemDoctor 不信任仅声明但未生效的配置名称。</summary>
public sealed record AiRuntimeDescriptor(
    string ModelProvider,
    string EmbeddingProvider,
    string RerankerProvider,
    bool ModelSandbox,
    bool EmbeddingSandbox,
    bool RerankerSandbox,
    int EmbeddingDimensions,
    string EmbeddingIndexVersion,
    bool Ready,
    string? NotReadyCode = null);

public sealed class AiProviderConfigurationException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>创建确定性或 OpenAI 兼容 HTTPS 适配器；真实供应商失败时绝不回退沙箱。</summary>
public static class AiProviderFactory
{
    public static IChatClient CreateChat(ModelProviderOptions options, HttpMessageHandler? handler = null)
    {
        if (options.Provider == AiProviderKind.Deterministic) return new DeterministicGroundedChatClient();
        ValidateRemote(options);
        return new OpenAiCompatibleChatClient(CreateHttpClient(options, handler), options);
    }

    public static ITextEmbeddingGenerator CreateEmbedding(EmbeddingProviderOptions options,
        HttpMessageHandler? handler = null)
    {
        if (options.Provider == AiProviderKind.Deterministic) return new DeterministicEmbeddingGenerator();
        ValidateRemote(options);
        if (options.Dimensions <= 0 || string.IsNullOrWhiteSpace(options.IndexVersion))
            throw new AiProviderConfigurationException("EMBEDDING_INDEX_BOUNDARY_INVALID",
                "真实嵌入必须显式配置正维度和索引版本，模型变化时必须重建新索引。");
        return new OpenAiCompatibleEmbeddingGenerator(CreateHttpClient(options, handler), options);
    }

    public static IEvidenceReranker CreateReranker(RerankerProviderOptions options,
        HttpMessageHandler? handler = null)
    {
        if (options.Provider == AiProviderKind.Lexical) return new LexicalEvidenceReranker();
        ValidateRemote(options);
        return new HttpSemanticEvidenceReranker(CreateHttpClient(options, handler), options);
    }

    private static void ValidateRemote(ModelProviderOptions options)
    {
        if (options.Provider is not (AiProviderKind.OpenAI or AiProviderKind.AzureOpenAI or AiProviderKind.HttpSemantic))
            throw new AiProviderConfigurationException("AI_PROVIDER_UNSUPPORTED", "AI 提供方不受支持。");
        if (!Uri.TryCreate(options.Endpoint, UriKind.Absolute, out var endpoint) || endpoint.Scheme != Uri.UriSchemeHttps)
            throw new AiProviderConfigurationException("AI_ENDPOINT_HTTPS_REQUIRED", "真实 AI 提供方 Endpoint 必须使用 HTTPS。");
        if (string.IsNullOrWhiteSpace(options.ApiKey) || string.IsNullOrWhiteSpace(options.Model))
            throw new AiProviderConfigurationException("AI_PROVIDER_CREDENTIALS_MISSING", "真实 AI 提供方缺少密钥或模型标识。");
        if (options.TimeoutSeconds is < 1 or > 120 || options.MaximumRetries is < 0 or > 3)
            throw new AiProviderConfigurationException("AI_RESILIENCE_OPTIONS_INVALID", "AI 超时或重试配置超出安全范围。");
    }

    private static HttpClient CreateHttpClient(ModelProviderOptions options, HttpMessageHandler? handler)
    {
        var client = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        client.BaseAddress = new Uri(options.Endpoint!.TrimEnd('/') + "/");
        client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
        if (options.Provider == AiProviderKind.AzureOpenAI)
            client.DefaultRequestHeaders.Add("api-key", options.ApiKey);
        else client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
        return client;
    }
}

internal static class AiHttpRetry
{
    public static async Task<JsonDocument> PostAsync(HttpClient client, string path, object payload, int retries,
        CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, path)
            {
                Content = new ByteArrayContent(bytes)
            };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.IsSuccessStatusCode)
                return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken),
                    cancellationToken: cancellationToken);
            var retryable = response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500;
            if (!retryable || attempt >= retries)
                throw new HttpRequestException($"AI_PROVIDER_HTTP_{(int)response.StatusCode}", null, response.StatusCode);
            await Task.Delay(TimeSpan.FromMilliseconds(50 * (attempt + 1)), cancellationToken);
        }
    }
}

public sealed class OpenAiCompatibleChatClient(HttpClient client, ModelProviderOptions providerOptions) : IChatClient
{
    private readonly ChatClientMetadata _metadata = new(providerOptions.Provider.ToString(), client.BaseAddress, providerOptions.Model);

    public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var payload = new
        {
            model = providerOptions.Model,
            messages = messages.Select(message => new { role = message.Role.Value, content = message.Text }).ToArray()
        };
        using var json = await AiHttpRetry.PostAsync(client, ChatPath(providerOptions), payload, providerOptions.MaximumRetries,
            cancellationToken);
        var text = json.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
        return new ChatResponse(new ChatMessage(ChatRole.Assistant, text ?? string.Empty));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await GetResponseAsync(messages, options, cancellationToken);
        yield return new ChatResponseUpdate(ChatRole.Assistant, response.Text);
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is null && serviceType.IsInstanceOfType(_metadata) ? _metadata : null;
    public void Dispose() => client.Dispose();

    private static string ChatPath(ModelProviderOptions value) => value.Provider == AiProviderKind.AzureOpenAI
        ? $"openai/deployments/{Uri.EscapeDataString(value.Model!)}/chat/completions?api-version=2024-10-21"
        : "v1/chat/completions";
}

public sealed class OpenAiCompatibleEmbeddingGenerator(HttpClient client, EmbeddingProviderOptions options)
    : ITextEmbeddingGenerator
{
    public int Dimensions => options.Dimensions;
    public async Task<float[]> GenerateAsync(string text, CancellationToken cancellationToken = default)
    {
        using var json = await AiHttpRetry.PostAsync(client,
            options.Provider == AiProviderKind.AzureOpenAI
                ? $"openai/deployments/{Uri.EscapeDataString(options.Model!)}/embeddings?api-version=2024-10-21"
                : "v1/embeddings",
            new { model = options.Model, input = text, dimensions = options.Dimensions }, options.MaximumRetries,
            cancellationToken);
        var vector = json.RootElement.GetProperty("data")[0].GetProperty("embedding")
            .EnumerateArray().Select(item => item.GetSingle()).ToArray();
        if (vector.Length != Dimensions)
            throw new InvalidOperationException("AI_EMBEDDING_DIMENSIONS_MISMATCH");
        return vector;
    }
}

public sealed class HttpSemanticEvidenceReranker(HttpClient client, RerankerProviderOptions options) : IEvidenceReranker
{
    public async Task<IReadOnlyList<Evidence>> RerankAsync(string question, IReadOnlyList<Evidence> evidence,
        CancellationToken cancellationToken = default)
    {
        using var json = await AiHttpRetry.PostAsync(client, "rerank",
            new { model = options.Model, query = question, documents = evidence.Select(x => x.Chunk.Content).ToArray() },
            options.MaximumRetries, cancellationToken);
        var scores = json.RootElement.GetProperty("scores").EnumerateArray().Select(x => x.GetDouble()).ToArray();
        if (scores.Length != evidence.Count) throw new InvalidOperationException("AI_RERANK_RESULT_INVALID");
        return evidence.Select((item, index) => new Evidence(item.Chunk, scores[index], item.RetrievalScore ?? item.Score))
            .OrderByDescending(item => item.Score).ToArray();
    }
}
