using System.Net;
using AiMentor.Application;
using AiMentor.Domain;
using AiMentor.Infrastructure;
using Xunit;

namespace AiMentor.Tests;

public sealed class OpenSearchKnowledgeRepositoryTests
{
    [Fact]
    public async Task InitializeShouldCreateMappingPipelineAndBulkDocuments()
    {
        var handler = new RecordingHandler(
            (HttpStatusCode.NotFound, "{}"),
            (HttpStatusCode.OK, "{}"),
            (HttpStatusCode.OK, "{}"),
            (HttpStatusCode.OK, "{\"errors\":false}"));
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://opensearch.test/") };
        using var repository = CreateRepository(client);

        await repository.InitializeAsync();

        Assert.Equal(4, handler.Requests.Count);
        Assert.Contains("\"type\":\"knn_vector\"", handler.Requests[1].Body, StringComparison.Ordinal);
        Assert.Contains("normalization-processor", handler.Requests[2].Body, StringComparison.Ordinal);
        Assert.Contains("\"tenant_id\":\"demo-beichen\"", handler.Requests[3].Body, StringComparison.Ordinal);
        Assert.Contains("\"embedding\":[", handler.Requests[3].Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SearchShouldPutTenantAndGroupsInsideBothHybridQueries()
    {
        var handler = new RecordingHandler(
            (HttpStatusCode.OK, "{}"),
            (HttpStatusCode.OK, "{}"),
            (HttpStatusCode.OK, "{\"errors\":false}"),
            (HttpStatusCode.OK, "{\"hits\":{\"hits\":[{\"_score\":0.91,\"_source\":{\"chunk_id\":\"BK-POL-002:0\",\"document_id\":\"BK-POL-002\",\"version\":\"3.0\",\"title\":\"身份规范\",\"section\":\"令牌\",\"content\":\"Access Token 默认有效期为 30 分钟。\",\"tenant_id\":\"demo-beichen\",\"allowed_groups\":[\"all-rnd\"],\"source_path\":\"synthetic\"}}]}}"));
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://opensearch.test/") };
        using var repository = CreateRepository(client);

        var results = await repository.SearchAsync("Access Token 多久", AccessContext.Create("demo-beichen", "user-1", ["all-rnd"]), 5);

        var searchRequest = handler.Requests[^1];
        Assert.Contains("aimentor-hybrid-v1", searchRequest.Uri, StringComparison.Ordinal);
        Assert.Equal(2, CountOccurrences(searchRequest.Body, "demo-beichen"));
        Assert.Equal(2, CountOccurrences(searchRequest.Body, "all-rnd"));
        Assert.Single(results);
        Assert.Equal("BK-POL-002", results[0].Chunk.DocumentId);
    }

    private static OpenSearchKnowledgeRepository CreateRepository(HttpClient client) => new(client, new SingleChunkSource(),
        new DeterministicEmbeddingGenerator(), new OpenSearchOptions());

    private static int CountOccurrences(string value, string fragment) =>
        (value.Length - value.Replace(fragment, string.Empty, StringComparison.Ordinal).Length) / fragment.Length;

    private sealed class SingleChunkSource : IKnowledgeChunkSource
    {
        public Task<IReadOnlyList<KnowledgeChunk>> ReadAllChunksAsync(CancellationToken cancellationToken = default)
        {
            IReadOnlyList<KnowledgeChunk> chunks =
            [
                new("BK-POL-002:0", "BK-POL-002", "3.0", "身份规范", "令牌", "Access Token 默认有效期为 30 分钟。",
                    "demo-beichen", new HashSet<string>(["all-rnd"], StringComparer.OrdinalIgnoreCase), "synthetic")
            ];
            return Task.FromResult(chunks);
        }
    }

    private sealed class RecordingHandler(params (HttpStatusCode Status, string Body)[] responses) : HttpMessageHandler
    {
        private readonly Queue<(HttpStatusCode Status, string Body)> _responses = new(responses);
        public List<RecordedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new RecordedRequest(request.Method.Method, request.RequestUri?.ToString() ?? string.Empty, body));
            var response = _responses.Dequeue();
            return new HttpResponseMessage(response.Status) { Content = new StringContent(response.Body) };
        }
    }

    private sealed record RecordedRequest(string Method, string Uri, string Body);
}
