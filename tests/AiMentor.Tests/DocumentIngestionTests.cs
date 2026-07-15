using AiMentor.Application;
using AiMentor.Domain;
using AiMentor.Infrastructure;
using Xunit;

namespace AiMentor.Tests;

public sealed class DocumentIngestionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"aimentor-ingestion-{Guid.NewGuid():N}");

    public DocumentIngestionTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task ParserShouldProduceElementsWithHierarchyReadingOrderAndTraceableAnchors()
    {
        var path = Write("policy.md", ValidDocument("## 令牌\n第一段。\n### 例外\n第二段。"));

        var parsed = await new MarkdownDocumentParser().ParseAsync(path, "policy.md");

        Assert.Equal(2, parsed.Elements.Count);
        Assert.Equal(["测试制度", "令牌"], parsed.Elements[0].SectionPath);
        Assert.Equal(["测试制度", "令牌", "例外"], parsed.Elements[1].SectionPath);
        Assert.Equal([0, 1], parsed.Elements.Select(element => element.ReadingOrder));
        Assert.All(parsed.Elements, element => Assert.StartsWith("policy.md#L", element.SourceAnchor));
    }

    [Fact]
    public async Task ChunkerShouldConsumeElementsAndPreserveStructureBoundary()
    {
        var path = Write("policy.md", ValidDocument("## 第一节\n内容一。\n## 第二节\n内容二。"));
        var parsed = await new MarkdownDocumentParser().ParseAsync(path, "policy.md");

        var chunks = new StructuredDocumentChunker().Chunk(parsed);

        Assert.Equal(2, chunks.Count);
        Assert.Equal("第一节", chunks[0].Section);
        Assert.Contains("#L", chunks[0].SourcePath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TopLevelHeadingShouldRemainANonEmptyVerifiableChunkSection()
    {
        var path = Write("policy.md", ValidDocument("# 测试制度\n可验证正文。"));
        var parsed = await new MarkdownDocumentParser().ParseAsync(path, "policy.md");

        var chunk = Assert.Single(new StructuredDocumentChunker().Chunk(parsed));

        Assert.Equal("测试制度", chunk.Section);
        Assert.Equal("可验证正文。", chunk.Content);
    }

    [Theory]
    [InlineData("document.pdf")]
    [InlineData("document.docx")]
    [InlineData("document.html")]
    public void RouterShouldExplicitlyRejectUnsupportedFormats(string path)
    {
        var router = new DocumentParserRouter(new MarkdownDocumentParser());

        var exception = Assert.Throws<NotSupportedException>(() => router.Resolve(path));

        Assert.Contains("仅支持 .md 和 .txt", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublishedDocumentWithoutBodyMustFailClosedAndNotEnterStatistics()
    {
        Write("empty.md", ValidDocument(string.Empty));
        using var repository = new MarkdownKnowledgeRepository(_root);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => repository.InitializeAsync());

        Assert.Contains("PARSE_EMPTY", exception.Message, StringComparison.Ordinal);
        Assert.Equal(new KnowledgeStatistics(0, 0), repository.Statistics);
    }

    [Fact]
    public async Task MissingRequiredMetadataMustFailClosed()
    {
        Write("invalid.md", "---\nid: DOC-1\nstatus: published\n---\n正文");
        using var repository = new MarkdownKnowledgeRepository(_root);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() => repository.InitializeAsync());

        Assert.Contains("version", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, repository.Statistics.Documents);
    }

    [Fact]
    public void PublishedElementWithoutSourceAnchorMustFailQualityGate()
    {
        var parsed = new ParsedKnowledgeDocument("DOC-1", "1.0", "标题", "tenant", new HashSet<string>(),
            "published", "doc.md", [new DocumentElement("e1", "text", ["标题"], "正文", 0, null)]);

        var decision = new RuleBasedParseQualityGate().Evaluate(parsed);

        Assert.False(decision.CanPublish);
        Assert.Equal("PARSE_SOURCE_ANCHOR_MISSING", decision.Code);
    }

    [Fact]
    public async Task DraftEmptyDocumentShouldRemainUnpublishedWithoutAffectingStatistics()
    {
        Write("draft.md", ValidDocument(string.Empty).Replace("status: published", "status: draft", StringComparison.Ordinal));
        using var repository = new MarkdownKnowledgeRepository(_root);

        await repository.InitializeAsync();

        Assert.Equal(new KnowledgeStatistics(0, 0), repository.Statistics);
    }

    private string Write(string name, string content)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, content);
        return path;
    }

    private static string ValidDocument(string body) => $"""
        ---
        id: DOC-1
        version: "1.0"
        title: 测试制度
        tenant_id: tenant-a
        acl_allow_groups: [all-rnd]
        status: published
        ---
        {body}
        """;

    public void Dispose() => Directory.Delete(_root, true);
}
