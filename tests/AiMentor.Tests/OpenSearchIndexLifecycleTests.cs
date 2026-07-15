using System.Net;
using System.Text.Json;
using AiMentor.Domain;
using AiMentor.Infrastructure;
using Xunit;

namespace AiMentor.Tests;

public sealed class OpenSearchIndexLifecycleTests
{
    [Fact]
    public async Task PublishValidatesNewPhysicalIndexBeforeAtomicAliasSwitch()
    {
        var handler = new QueueHandler(
            Reply(HttpStatusCode.NotFound), Reply(), Reply(), Reply(body: "{\"errors\":false}"),
            Reply(body: "{\"count\":1}"), Mapping("aimentor-knowledge-v2-build7", 256), SearchHit(),
            Reply(HttpStatusCode.NotFound), Reply());
        var service = Service(handler);

        var manifest = await service.PublishAsync("v2", "build7", [Chunk()]);

        Assert.Equal("aimentor-knowledge-v2-build7", manifest.PhysicalIndex);
        var alias = handler.Requests[^1];
        Assert.Equal("POST", alias.Method);
        Assert.EndsWith("/_aliases", alias.Uri, StringComparison.Ordinal);
        Assert.Contains("aimentor-knowledge-current", alias.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("aimentor-knowledge-v1", handler.Requests[3].Body, StringComparison.Ordinal);
        Assert.Contains("tenant-a", handler.Requests[6].Body, StringComparison.Ordinal);
        Assert.Contains("readers", handler.Requests[6].Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CountMismatchFailsBeforeAnyAliasRequest()
    {
        var handler = new QueueHandler(Reply(HttpStatusCode.NotFound), Reply(), Reply(),
            Reply(body: "{\"errors\":false}"), Reply(body: "{\"count\":0}"));
        var service = Service(handler);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.PublishAsync("v2", "bad", [Chunk()]));

        Assert.Equal("OPENSEARCH_PUBLISH_COUNT_MISMATCH", exception.Message);
        Assert.DoesNotContain(handler.Requests, request => request.Uri.EndsWith("/_aliases", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DimensionMismatchFailsClosed()
    {
        var handler = new QueueHandler(Reply(HttpStatusCode.NotFound), Reply(), Reply(),
            Reply(body: "{\"errors\":false}"), Reply(body: "{\"count\":1}"),
            Mapping("aimentor-knowledge-v2-bad", 768));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Service(handler).PublishAsync("v2", "bad", [Chunk()]));

        Assert.Equal("OPENSEARCH_PUBLISH_DIMENSION_MISMATCH", exception.Message);
        Assert.DoesNotContain(handler.Requests, request => request.Uri.EndsWith("/_aliases", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RollbackSwapsCurrentAndPreviousInOneAliasesRequest()
    {
        var handler = new QueueHandler(Reply(body: AliasBody()), Reply());
        await Service(handler).RollbackAsync();

        Assert.Equal(2, handler.Requests.Count);
        var body = handler.Requests[1].Body;
        Assert.Equal(4, Count(body, "\"index\""));
        Assert.Contains("aimentor-knowledge-current", body, StringComparison.Ordinal);
        Assert.Contains("aimentor-knowledge-previous", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CurrentAndPreviousIndexesCannotBeDeleted()
    {
        var handler = new QueueHandler(Reply(body: AliasBody()));
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Service(handler).DeleteIndexAsync("aimentor-knowledge-v2-new"));

        Assert.Equal("OPENSEARCH_PROTECTED_INDEX_DELETE_REFUSED", exception.Message);
        Assert.Single(handler.Requests);
    }

    private static OpenSearchIndexLifecycleService Service(HttpMessageHandler handler) => new(
        new HttpClient(handler) { BaseAddress = new Uri("http://opensearch.test/") },
        new DeterministicEmbeddingGenerator(), new OpenSearchIndexLifecycleOptions());

    private static KnowledgeChunk Chunk() => new("chunk-1", "DOC-1", "1.0", "标题", "章节", "内容",
        "tenant-a", new HashSet<string>(["readers"]), "source.md");

    private static (HttpStatusCode, string) Reply(HttpStatusCode status = HttpStatusCode.OK, string body = "{}") =>
        (status, body);
    private static (HttpStatusCode, string) Mapping(string index, int dimensions) => Reply(body:
        JsonSerializer.Serialize(new Dictionary<string, object>
        {
            [index] = new { mappings = new { properties = new { embedding = new { dimension = dimensions } } } }
        }));
    private static (HttpStatusCode, string) SearchHit() => Reply(body:
        "{\"hits\":{\"hits\":[{\"_source\":{\"tenant_id\":\"tenant-a\",\"allowed_groups\":[\"readers\"],\"document_id\":\"DOC-1\"}}]}}");
    private static string AliasBody() =>
        "{\"aimentor-knowledge-v2-new\":{\"aliases\":{\"aimentor-knowledge-current\":{}}},\"aimentor-knowledge-v1-old\":{\"aliases\":{\"aimentor-knowledge-previous\":{}}}}";
    private static int Count(string value, string fragment) =>
        (value.Length - value.Replace(fragment, string.Empty, StringComparison.Ordinal).Length) / fragment.Length;

    private sealed class QueueHandler(params (HttpStatusCode Status, string Body)[] responses) : HttpMessageHandler
    {
        private readonly Queue<(HttpStatusCode Status, string Body)> _responses = new(responses);
        public List<Request> Requests { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new Request(request.Method.Method, request.RequestUri!.ToString(), body));
            var response = _responses.Dequeue();
            return new HttpResponseMessage(response.Status) { Content = new StringContent(response.Body) };
        }
    }

    private sealed record Request(string Method, string Uri, string Body);
}
