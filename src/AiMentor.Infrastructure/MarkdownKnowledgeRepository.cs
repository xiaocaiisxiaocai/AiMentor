using System.Text.RegularExpressions;
using AiMentor.Application;
using AiMentor.Domain;

namespace AiMentor.Infrastructure;

/// <summary>加载本地 Markdown 知识并在内存中执行租户与 ACL 前置过滤检索。</summary>
public sealed class MarkdownKnowledgeRepository : IKnowledgeRepository, IKnowledgeChunkSource, IDisposable
{
    private readonly string _rootPath;
    private readonly IDocumentParserRouter _parserRouter;
    private readonly IDocumentChunker _chunker;
    private readonly IParseQualityGate _qualityGate;
    private KnowledgeDocument[] _documents = [];
    private readonly SemaphoreSlim _initializationLock = new(1, 1);
    private int _disposed;

    public KnowledgeStatistics Statistics { get; private set; } = new(0, 0);

    public MarkdownKnowledgeRepository(string rootPath)
        : this(rootPath, new DocumentParserRouter(new MarkdownDocumentParser()), new StructuredDocumentChunker(),
            new RuleBasedParseQualityGate()) { }

    public MarkdownKnowledgeRepository(string rootPath, IDocumentParserRouter parserRouter, IDocumentChunker chunker,
        IParseQualityGate qualityGate)
    {
        _rootPath = Path.GetFullPath(rootPath);
        _parserRouter = parserRouter;
        _chunker = chunker;
        _qualityGate = qualityGate;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_documents.Length > 0) return;
        await _initializationLock.WaitAsync(cancellationToken);
        try
        {
            if (_documents.Length > 0) return;
            if (!Directory.Exists(_rootPath)) throw new DirectoryNotFoundException($"知识目录不存在：{_rootPath}");

            var documents = new List<KnowledgeDocument>();
            var paths = Directory.EnumerateFiles(_rootPath, "*", SearchOption.AllDirectories)
                .Where(path => Path.GetExtension(path).Equals(".md", StringComparison.OrdinalIgnoreCase)
                    || Path.GetExtension(path).Equals(".txt", StringComparison.OrdinalIgnoreCase))
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToArray();
            foreach (var path in paths)
            {
                var parser = _parserRouter.Resolve(path);
                var parsed = await parser.ParseAsync(path, Path.GetRelativePath(_rootPath, path), cancellationToken);
                if (!string.Equals(parsed.Status, "published", StringComparison.OrdinalIgnoreCase)) continue;
                var quality = _qualityGate.Evaluate(parsed);
                if (!quality.CanPublish)
                {
                    // 已声明发布的文档若被静默忽略，运维侧会误以为知识已经可用，因此必须失败关闭。
                    throw new InvalidDataException($"{path} 未通过解析质量门禁：{quality.Code}；{quality.Message}");
                }
                var chunks = _chunker.Chunk(parsed);
                if (chunks.Count == 0)
                    throw new InvalidDataException($"{path} 切分后没有可发布分块，禁止计入知识统计。");
                documents.Add(new KnowledgeDocument(parsed.Id, parsed.Version, parsed.Title, parsed.TenantId,
                    parsed.AllowedGroups, parsed.Status, parsed.SourcePath, chunks));
            }

            _documents = documents.OrderBy(x => x.Id, StringComparer.Ordinal).ToArray();
            Statistics = new KnowledgeStatistics(_documents.Length, _documents.Sum(x => x.Chunks.Count));
        }
        finally
        {
            _initializationLock.Release();
        }
    }

    public async Task<IReadOnlyList<Evidence>> SearchAsync(string query, AccessContext access, int limit, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        var queryTokens = Tokenize(query);
        if (queryTokens.Count == 0) return [];

        // 权限过滤发生在相关性评分之前，禁止“先召回、后过滤”造成元数据侧漏。
        return _documents
            .Where(document => string.Equals(document.TenantId, access.TenantId, StringComparison.OrdinalIgnoreCase))
            .Where(document => document.AllowedGroups.Count == 0 || document.AllowedGroups.Overlaps(access.Groups))
            .SelectMany(document => document.Chunks)
            .Select(chunk => new Evidence(chunk, Score(queryTokens, Tokenize($"{chunk.Title} {chunk.Section} {chunk.Content}"))))
            .Where(item => item.Score > 0)
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Chunk.DocumentId, StringComparer.Ordinal)
            .Take(Math.Max(1, limit))
            .ToArray();
    }

    public async Task<IReadOnlyList<KnowledgeChunk>> ReadAllChunksAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        return _documents.SelectMany(document => document.Chunks).ToArray();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) _initializationLock.Dispose();
    }

    private static HashSet<string> Tokenize(string value)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in Regex.Matches(value.ToLowerInvariant(), @"[a-z0-9][a-z0-9._-]*|[\u4e00-\u9fff]+"))
        {
            var token = match.Value;
            if (Regex.IsMatch(token, @"^[\u4e00-\u9fff]+$"))
            {
                if (token.Length == 1) result.Add(token);
                for (var i = 0; i < token.Length - 1; i++) result.Add(token.Substring(i, 2));
            }
            else result.Add(token);
        }
        return result;
    }

    private static double Score(HashSet<string> query, HashSet<string> document)
    {
        var overlap = query.Count(document.Contains);
        if (overlap == 0) return 0;
        return (double)overlap / query.Count;
    }
}
