using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AiMentor.Application;
using AiMentor.Domain;

namespace AiMentor.Infrastructure;

public sealed record OpenSearchSmokeQuery(string Query, string TenantId, IReadOnlySet<string> Groups,
    string ExpectedDocumentId);

public sealed record OpenSearchIndexLifecycleOptions
{
    public string IndexPrefix { get; init; } = "aimentor-knowledge";
    public string CurrentAlias { get; init; } = "aimentor-knowledge-current";
    public string PreviousAlias { get; init; } = "aimentor-knowledge-previous";
    public string SearchPipelineName { get; init; } = "aimentor-hybrid-v1";
    public double LexicalWeight { get; init; } = 0.45;
    public double VectorWeight { get; init; } = 0.55;
    public IReadOnlyList<OpenSearchSmokeQuery> CriticalSmokeQueries { get; init; } = [];
}

public sealed record OpenSearchIndexManifest(string SchemaVersion, string PhysicalIndex, string Version, string Build,
    int VectorDimensions, int ExpectedDocuments, int ExpectedChunks, DateTimeOffset PublishedAt);

/// <summary>管理不可变知识索引的发布、原子 alias 切换和回滚。</summary>
public sealed partial class OpenSearchIndexLifecycleService(HttpClient client, ITextEmbeddingGenerator embeddings,
    OpenSearchIndexLifecycleOptions options)
{
    public async Task<OpenSearchIndexManifest> PublishAsync(string version, string build,
        IReadOnlyList<KnowledgeChunk> chunks, CancellationToken cancellationToken = default)
    {
        ValidateConfiguration();
        if (chunks.Count == 0) throw new InvalidOperationException("OPENSEARCH_PUBLISH_EMPTY_CORPUS");
        var physicalIndex = PhysicalIndex(version, build);
        await EnsureIndexDoesNotExistAsync(physicalIndex, cancellationToken);
        await CreateIndexAsync(physicalIndex, cancellationToken);
        await EnsurePipelineAsync(cancellationToken);
        await BulkAsync(physicalIndex, chunks, cancellationToken);
        // 所有门禁必须先于 alias 切换；失败时新索引保持孤立，线上读路径完全不变。
        await VerifyCountAsync(physicalIndex, chunks.Count, cancellationToken);
        await VerifyDimensionsAsync(physicalIndex, cancellationToken);
        await VerifyAclSampleAsync(physicalIndex, chunks, cancellationToken);
        await VerifyCriticalSmokeAsync(physicalIndex, cancellationToken);
        await SwitchAliasesAsync(physicalIndex, cancellationToken);
        return new OpenSearchIndexManifest("1", physicalIndex, version, build, embeddings.Dimensions,
            chunks.Select(item => item.DocumentId).Distinct(StringComparer.Ordinal).Count(), chunks.Count,
            DateTimeOffset.UtcNow);
    }

    public async Task RollbackAsync(CancellationToken cancellationToken = default)
    {
        var aliases = await ReadProtectedAliasesAsync(cancellationToken);
        var current = aliases.GetValueOrDefault(options.CurrentAlias);
        var previous = aliases.GetValueOrDefault(options.PreviousAlias);
        if (current is null || previous is null) throw new InvalidOperationException("OPENSEARCH_ROLLBACK_NOT_READY");
        var actions = new object[]
        {
            Remove(current, options.CurrentAlias), Remove(previous, options.PreviousAlias),
            Add(previous, options.CurrentAlias), Add(current, options.PreviousAlias)
        };
        await PostAliasesAsync(actions, cancellationToken);
    }

    public async Task DeleteIndexAsync(string physicalIndex, CancellationToken cancellationToken = default)
    {
        var aliases = await ReadProtectedAliasesAsync(cancellationToken);
        if (aliases.Values.Contains(physicalIndex, StringComparer.Ordinal))
            throw new InvalidOperationException("OPENSEARCH_PROTECTED_INDEX_DELETE_REFUSED");
        using var response = await client.DeleteAsync(physicalIndex, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    private async Task SwitchAliasesAsync(string physicalIndex, CancellationToken cancellationToken)
    {
        var aliases = await ReadProtectedAliasesAsync(cancellationToken);
        var actions = new List<object>();
        if (aliases.TryGetValue(options.PreviousAlias, out var oldPrevious))
            actions.Add(Remove(oldPrevious, options.PreviousAlias));
        if (aliases.TryGetValue(options.CurrentAlias, out var oldCurrent))
        {
            actions.Add(Remove(oldCurrent, options.CurrentAlias));
            actions.Add(Add(oldCurrent, options.PreviousAlias));
        }
        actions.Add(Add(physicalIndex, options.CurrentAlias));
        await PostAliasesAsync(actions, cancellationToken);
    }

    private async Task<Dictionary<string, string>> ReadProtectedAliasesAsync(CancellationToken cancellationToken)
    {
        // 查询完整 alias 视图后本地筛选，避免 current 存在但 previous 缺失时组合查询整体返回 404。
        using var response = await client.GetAsync("_alias", cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) return [];
        using var json = await ReadJsonAsync(response, cancellationToken);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var index in json.RootElement.EnumerateObject())
        foreach (var alias in index.Value.GetProperty("aliases").EnumerateObject())
            if (alias.Name == options.CurrentAlias || alias.Name == options.PreviousAlias)
            {
                if (!result.TryAdd(alias.Name, index.Name))
                    throw new InvalidOperationException("OPENSEARCH_ALIAS_MULTIPLE_TARGETS");
            }
        return result;
    }

    private async Task PostAliasesAsync(IEnumerable<object> actions, CancellationToken cancellationToken)
    {
        using var response = await client.PostAsJsonAsync("_aliases", new { actions }, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    private async Task EnsureIndexDoesNotExistAsync(string index, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Head, index);
        using var response = await client.SendAsync(request, cancellationToken);
        if (response.IsSuccessStatusCode) throw new InvalidOperationException("OPENSEARCH_IMMUTABLE_INDEX_EXISTS");
        if (response.StatusCode != HttpStatusCode.NotFound) await EnsureSuccessAsync(response, cancellationToken);
    }

    private async Task CreateIndexAsync(string index, CancellationToken cancellationToken)
    {
        var properties = new Dictionary<string, object?>
        {
            ["chunk_id"] = Field("keyword"), ["document_id"] = Field("keyword"), ["version"] = Field("keyword"),
            ["title"] = Field("text"), ["section"] = Field("text"), ["content"] = Field("text"),
            ["tenant_id"] = Field("keyword"), ["allowed_groups"] = Field("keyword"),
            ["source_path"] = Field("keyword", false),
            ["embedding"] = new { type = "knn_vector", dimension = embeddings.Dimensions,
                method = new { name = "hnsw", engine = "lucene", space_type = "cosinesimil" } }
        };
        using var response = await client.PutAsJsonAsync(index, new
        {
            settings = new { index = new { knn = true } }, mappings = new { dynamic = "strict", properties }
        }, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    private async Task EnsurePipelineAsync(CancellationToken cancellationToken)
    {
        using var response = await client.PutAsJsonAsync($"_search/pipeline/{options.SearchPipelineName}", new
        {
            description = "AiMentor blue-green hybrid retrieval",
            phase_results_processors = new object[] { new Dictionary<string, object?>
            {
                ["normalization-processor"] = new
                {
                    normalization = new { technique = "min_max" }, combination = new
                    {
                        technique = "arithmetic_mean", parameters = new
                        {
                            weights = new[] { options.LexicalWeight, options.VectorWeight }
                        }
                    }
                }
            } }
        }, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    private async Task BulkAsync(string index, IReadOnlyList<KnowledgeChunk> chunks,
        CancellationToken cancellationToken)
    {
        var payload = new StringBuilder();
        foreach (var chunk in chunks)
        {
            payload.AppendLine(JsonSerializer.Serialize(new { index = new { _index = index, _id = chunk.Id } }));
            var vector = await embeddings.GenerateAsync($"{chunk.Title}\n{chunk.Section}\n{chunk.Content}", cancellationToken);
            payload.AppendLine(JsonSerializer.Serialize(new
            {
                chunk_id = chunk.Id, document_id = chunk.DocumentId, version = chunk.Version, title = chunk.Title,
                section = chunk.Section, content = chunk.Content, tenant_id = chunk.TenantId,
                allowed_groups = chunk.AllowedGroups.Order(StringComparer.Ordinal), source_path = chunk.SourcePath,
                embedding = vector
            }));
        }
        using var response = await client.PostAsync("_bulk?refresh=true",
            new StringContent(payload.ToString(), Encoding.UTF8, "application/x-ndjson"), cancellationToken);
        using var json = await ReadJsonAsync(response, cancellationToken);
        if (json.RootElement.TryGetProperty("errors", out var errors) && errors.GetBoolean())
            throw new InvalidOperationException("OPENSEARCH_BULK_PARTIAL_FAILURE");
    }

    private async Task VerifyCountAsync(string index, int expected, CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync($"{index}/_count", cancellationToken);
        using var json = await ReadJsonAsync(response, cancellationToken);
        if (json.RootElement.GetProperty("count").GetInt32() != expected)
            throw new InvalidOperationException("OPENSEARCH_PUBLISH_COUNT_MISMATCH");
    }

    private async Task VerifyDimensionsAsync(string index, CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync($"{index}/_mapping", cancellationToken);
        using var json = await ReadJsonAsync(response, cancellationToken);
        var mapping = json.RootElement.GetProperty(index).GetProperty("mappings").GetProperty("properties")
            .GetProperty("embedding").GetProperty("dimension").GetInt32();
        if (mapping != embeddings.Dimensions)
            throw new InvalidOperationException("OPENSEARCH_PUBLISH_DIMENSION_MISMATCH");
    }

    private async Task VerifyAclSampleAsync(string index, IReadOnlyList<KnowledgeChunk> chunks,
        CancellationToken cancellationToken)
    {
        var sample = chunks[0];
        var group = sample.AllowedGroups.Order(StringComparer.Ordinal).FirstOrDefault()
                    ?? throw new InvalidOperationException("OPENSEARCH_PUBLISH_ACL_SAMPLE_INVALID");
        using var response = await client.PostAsJsonAsync($"{index}/_search", new
        {
            size = 10, query = new { @bool = new { filter = new object[]
            {
                new { term = new Dictionary<string, string> { ["tenant_id"] = sample.TenantId } },
                new { term = new Dictionary<string, string> { ["allowed_groups"] = group } }
            } } }
        }, cancellationToken);
        using var json = await ReadJsonAsync(response, cancellationToken);
        var sources = json.RootElement.GetProperty("hits").GetProperty("hits").EnumerateArray()
            .Select(hit => hit.GetProperty("_source")).ToArray();
        if (sources.Length == 0 || sources.Any(source => source.GetProperty("tenant_id").GetString() != sample.TenantId
            || !source.GetProperty("allowed_groups").EnumerateArray().Any(item => item.GetString() == group)))
            throw new InvalidOperationException("OPENSEARCH_PUBLISH_ACL_SMOKE_FAILED");
    }

    private async Task VerifyCriticalSmokeAsync(string index, CancellationToken cancellationToken)
    {
        foreach (var smoke in options.CriticalSmokeQueries)
        {
            var vector = await embeddings.GenerateAsync(smoke.Query, cancellationToken);
            using var response = await client.PostAsJsonAsync(
                $"{index}/_search?search_pipeline={options.SearchPipelineName}", new
                {
                    size = 5, query = new { hybrid = new { queries = new object[]
                    {
                        new { @bool = new { must = new { match = new { content = smoke.Query } }, filter = Filters(smoke) } },
                        new { knn = new { embedding = new { vector, k = 20, filter = new { @bool = new { filter = Filters(smoke) } } } } }
                    } } }
                }, cancellationToken);
            using var json = await ReadJsonAsync(response, cancellationToken);
            if (!json.RootElement.GetProperty("hits").GetProperty("hits").EnumerateArray().Any(hit =>
                    hit.GetProperty("_source").GetProperty("document_id").GetString() == smoke.ExpectedDocumentId))
                throw new InvalidOperationException("OPENSEARCH_PUBLISH_CRITICAL_SMOKE_FAILED");
        }
    }

    private static object[] Filters(OpenSearchSmokeQuery smoke) =>
    [
        new { term = new Dictionary<string, string> { ["tenant_id"] = smoke.TenantId } },
        new { terms = new Dictionary<string, string[]> { ["allowed_groups"] = smoke.Groups.Order(StringComparer.Ordinal).ToArray() } }
    ];

    private void ValidateConfiguration()
    {
        if (client.BaseAddress is null || embeddings.Dimensions <= 0 || !SafeName().IsMatch(options.IndexPrefix)
            || !SafeName().IsMatch(options.CurrentAlias) || !SafeName().IsMatch(options.PreviousAlias)
            || options.CurrentAlias == options.PreviousAlias)
            throw new InvalidOperationException("OPENSEARCH_LIFECYCLE_CONFIGURATION_INVALID");
    }

    private string PhysicalIndex(string version, string build)
    {
        var name = $"{options.IndexPrefix}-{version}-{build}".ToLowerInvariant();
        if (!SafeName().IsMatch(name)) throw new InvalidOperationException("OPENSEARCH_PHYSICAL_INDEX_NAME_INVALID");
        return name;
    }

    private static object Add(string index, string alias) => new { add = new { index, alias } };
    private static object Remove(string index, string alias) => new { remove = new { index, alias } };
    private static Dictionary<string, object> Field(string type, bool index = true) => new() { ["type"] = type, ["index"] = index };

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        await EnsureSuccessAsync(response, cancellationToken);
        return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken),
            cancellationToken: cancellationToken);
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new HttpRequestException($"OPENSEARCH_HTTP_{(int)response.StatusCode}:{body}", null,
            response.StatusCode);
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeName();
}
