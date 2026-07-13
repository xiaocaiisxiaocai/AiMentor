using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using AiMentor.Application;
using AiMentor.Domain;

namespace AiMentor.Infrastructure;

/// <summary>配置 OpenSearch 索引、混合检索管线和启动同步行为。</summary>
public sealed class OpenSearchOptions
{
    public string IndexName { get; init; } = "aimentor-knowledge-v1";
    public string SearchPipelineName { get; init; } = "aimentor-hybrid-v1";
    public bool SynchronizeOnStartup { get; init; } = true;
    public double LexicalWeight { get; init; } = 0.45;
    public double VectorWeight { get; init; } = 0.55;
}

public sealed partial class OpenSearchKnowledgeRepository(
    HttpClient httpClient,
    IKnowledgeChunkSource source,
    ITextEmbeddingGenerator embeddings,
    OpenSearchOptions options) : IKnowledgeRepository, IDisposable
{
    private readonly SemaphoreSlim _initializationLock = new(1, 1);
    private volatile bool _initialized;

    public KnowledgeStatistics Statistics { get; private set; } = new(0, 0);

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized) return;
        await _initializationLock.WaitAsync(cancellationToken);
        try
        {
            if (_initialized) return;
            ValidateOptions();
            await EnsureIndexAsync(cancellationToken);
            await EnsureSearchPipelineAsync(cancellationToken);
            var chunks = await source.ReadAllChunksAsync(cancellationToken);
            if (options.SynchronizeOnStartup) await SynchronizeAsync(chunks, cancellationToken);
            Statistics = new KnowledgeStatistics(chunks.Select(chunk => chunk.DocumentId).Distinct(StringComparer.Ordinal).Count(), chunks.Count);
            _initialized = true;
        }
        finally
        {
            _initializationLock.Release();
        }
    }

    public async Task<IReadOnlyList<Evidence>> SearchAsync(string query, AccessContext access, int limit,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        if (access.Groups.Count == 0) return [];

        var vector = await embeddings.GenerateAsync(query, cancellationToken);
        var filter = CreateAccessFilter(access);
        var lexical = new Dictionary<string, object?>
        {
            ["bool"] = new Dictionary<string, object?>
            {
                ["must"] = new Dictionary<string, object?>
                {
                    ["multi_match"] = new Dictionary<string, object?>
                    {
                        ["query"] = query,
                        ["fields"] = new[] { "title^3", "section^2", "content" },
                        ["type"] = "best_fields"
                    }
                },
                ["filter"] = filter
            }
        };
        var vectorQuery = new Dictionary<string, object?>
        {
            ["knn"] = new Dictionary<string, object?>
            {
                ["embedding"] = new Dictionary<string, object?>
                {
                    ["vector"] = vector,
                    ["k"] = Math.Max(limit * 4, 20),
                    ["filter"] = new Dictionary<string, object?> { ["bool"] = new Dictionary<string, object?> { ["filter"] = filter } }
                }
            }
        };
        var body = new Dictionary<string, object?>
        {
            ["size"] = Math.Max(1, limit),
            ["_source"] = new Dictionary<string, object?> { ["excludes"] = new[] { "embedding" } },
            ["query"] = new Dictionary<string, object?>
            {
                ["hybrid"] = new Dictionary<string, object?> { ["queries"] = new object[] { lexical, vectorQuery } }
            }
        };

        var path = $"{options.IndexName}/_search?search_pipeline={Uri.EscapeDataString(options.SearchPipelineName)}";
        using var response = await httpClient.PostAsJsonAsync(path, body, cancellationToken);
        using var payload = await ReadSuccessfulJsonAsync(response, cancellationToken);
        var hits = payload.RootElement.GetProperty("hits").GetProperty("hits").EnumerateArray().ToArray();
        return hits.Select(hit =>
        {
            var indexed = hit.GetProperty("_source").Deserialize(SerializationContext.Default.IndexedChunk)
                ?? throw new InvalidDataException("OpenSearch 返回了无效知识分块。");
            var score = hit.TryGetProperty("_score", out var scoreElement) && scoreElement.ValueKind == JsonValueKind.Number
                ? scoreElement.GetDouble() : 0;
            return new Evidence(indexed.ToDomain(), Math.Clamp(score, 0, 1));
        }).ToArray();
    }

    private async Task EnsureIndexAsync(CancellationToken cancellationToken)
    {
        using var headRequest = new HttpRequestMessage(HttpMethod.Head, options.IndexName);
        using var headResponse = await httpClient.SendAsync(headRequest, cancellationToken);
        if (headResponse.IsSuccessStatusCode) return;
        if (headResponse.StatusCode != HttpStatusCode.NotFound)
        {
            await ThrowOpenSearchErrorAsync(headResponse, cancellationToken);
        }

        var body = new Dictionary<string, object?>
        {
            ["settings"] = new Dictionary<string, object?> { ["index"] = new Dictionary<string, object?> { ["knn"] = true } },
            ["mappings"] = new Dictionary<string, object?>
            {
                ["dynamic"] = "strict",
                ["properties"] = new Dictionary<string, object?>
                {
                    ["chunk_id"] = Field("keyword"),
                    ["document_id"] = Field("keyword"),
                    ["version"] = Field("keyword"),
                    ["title"] = Field("text"),
                    ["section"] = Field("text"),
                    ["content"] = Field("text"),
                    ["tenant_id"] = Field("keyword"),
                    ["allowed_groups"] = Field("keyword"),
                    ["source_path"] = Field("keyword", false),
                    ["embedding"] = new Dictionary<string, object?>
                    {
                        ["type"] = "knn_vector",
                        ["dimension"] = embeddings.Dimensions,
                        ["method"] = new Dictionary<string, object?> { ["name"] = "hnsw", ["engine"] = "lucene", ["space_type"] = "cosinesimil" }
                    }
                }
            }
        };
        using var response = await httpClient.PutAsJsonAsync(options.IndexName, body, cancellationToken);
        using var responsePayload = await ReadSuccessfulJsonAsync(response, cancellationToken);
    }

    private async Task EnsureSearchPipelineAsync(CancellationToken cancellationToken)
    {
        var body = new Dictionary<string, object?>
        {
            ["description"] = "AiMentor BM25 与向量检索归一化融合",
            ["phase_results_processors"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["normalization-processor"] = new Dictionary<string, object?>
                    {
                        ["normalization"] = new Dictionary<string, object?> { ["technique"] = "min_max" },
                        ["combination"] = new Dictionary<string, object?>
                        {
                            ["technique"] = "arithmetic_mean",
                            ["parameters"] = new Dictionary<string, object?> { ["weights"] = new[] { options.LexicalWeight, options.VectorWeight } }
                        }
                    }
                }
            }
        };
        using var response = await httpClient.PutAsJsonAsync($"_search/pipeline/{Uri.EscapeDataString(options.SearchPipelineName)}", body, cancellationToken);
        using var responsePayload = await ReadSuccessfulJsonAsync(response, cancellationToken);
    }

    private async Task SynchronizeAsync(IReadOnlyList<KnowledgeChunk> chunks, CancellationToken cancellationToken)
    {
        const int batchSize = 200;
        for (var offset = 0; offset < chunks.Count; offset += batchSize)
        {
            var batch = chunks.Skip(offset).Take(batchSize).ToArray();
            var payload = new StringBuilder();
            foreach (var chunk in batch)
            {
                payload.AppendLine(JsonSerializer.Serialize(new { index = new { _index = options.IndexName, _id = chunk.Id } }));
                var vector = await embeddings.GenerateAsync($"{chunk.Title}\n{chunk.Section}\n{chunk.Content}", cancellationToken);
                payload.AppendLine(JsonSerializer.Serialize(IndexedChunk.FromDomain(chunk, vector), SerializationContext.Default.IndexedChunk));
            }
            using var content = new StringContent(payload.ToString(), Encoding.UTF8, "application/x-ndjson");
            using var response = await httpClient.PostAsync("_bulk?refresh=true", content, cancellationToken);
            using var json = await ReadSuccessfulJsonAsync(response, cancellationToken);
            if (json.RootElement.TryGetProperty("errors", out var errors) && errors.GetBoolean())
            {
                throw new InvalidOperationException("OpenSearch 批量摄取返回部分失败，请检查 items 错误详情。");
            }
        }
    }

    private static object[] CreateAccessFilter(AccessContext access) =>
    [
        new Dictionary<string, object?> { ["term"] = new Dictionary<string, object?> { ["tenant_id"] = access.TenantId } },
        new Dictionary<string, object?> { ["terms"] = new Dictionary<string, object?> { ["allowed_groups"] = access.Groups.Order(StringComparer.Ordinal).ToArray() } }
    ];

    private static Dictionary<string, object?> Field(string type, bool index = true) => new() { ["type"] = type, ["index"] = index };

    private static async Task<JsonDocument> ReadSuccessfulJsonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode) await ThrowOpenSearchErrorAsync(response, cancellationToken);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    }

    private static async Task ThrowOpenSearchErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new HttpRequestException($"OpenSearch 请求失败：{(int)response.StatusCode} {response.ReasonPhrase}；{body}", null, response.StatusCode);
    }

    private void ValidateOptions()
    {
        if (httpClient.BaseAddress is null) throw new InvalidOperationException("OpenSearch HttpClient 必须配置 BaseAddress。");
        if (string.IsNullOrWhiteSpace(options.IndexName) || string.IsNullOrWhiteSpace(options.SearchPipelineName))
            throw new InvalidOperationException("OpenSearch 索引名和搜索管线名不能为空。");
        if (!SafeResourceName().IsMatch(options.IndexName) || !SafeResourceName().IsMatch(options.SearchPipelineName))
            throw new InvalidOperationException("OpenSearch 索引名和搜索管线名只能包含小写字母、数字、点、下划线和连字符。");
        if (options.LexicalWeight is < 0 or > 1 || options.VectorWeight is < 0 or > 1 ||
            Math.Abs(options.LexicalWeight + options.VectorWeight - 1) > 0.0001)
            throw new InvalidOperationException("OpenSearch 混合检索权重必须位于 0 到 1 且总和为 1。");
    }

    public void Dispose()
    {
        _initializationLock.Dispose();
    }

    [GeneratedRegex(@"^[a-z0-9][a-z0-9._-]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeResourceName();

    internal sealed record IndexedChunk(
        [property: JsonPropertyName("chunk_id")] string ChunkId,
        [property: JsonPropertyName("document_id")] string DocumentId,
        [property: JsonPropertyName("version")] string Version,
        [property: JsonPropertyName("title")] string Title,
        [property: JsonPropertyName("section")] string Section,
        [property: JsonPropertyName("content")] string Content,
        [property: JsonPropertyName("tenant_id")] string TenantId,
        [property: JsonPropertyName("allowed_groups")] string[] AllowedGroups,
        [property: JsonPropertyName("source_path")] string SourcePath,
        [property: JsonPropertyName("embedding")] float[]? Embedding)
    {
        public static IndexedChunk FromDomain(KnowledgeChunk chunk, float[] embedding) => new(chunk.Id, chunk.DocumentId, chunk.Version,
            chunk.Title, chunk.Section, chunk.Content, chunk.TenantId, chunk.AllowedGroups.Order(StringComparer.Ordinal).ToArray(), chunk.SourcePath, embedding);

        public KnowledgeChunk ToDomain() => new(ChunkId, DocumentId, Version, Title, Section, Content, TenantId,
            new HashSet<string>(AllowedGroups, StringComparer.OrdinalIgnoreCase), SourcePath);
    }

    [JsonSerializable(typeof(IndexedChunk))]
    private sealed partial class SerializationContext : JsonSerializerContext;
}
