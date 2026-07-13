using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using AiMentor.Application;
using AiMentor.Domain;

namespace AiMentor.Infrastructure;

public sealed class MarkdownKnowledgeRepository(string rootPath) : IKnowledgeRepository, IDisposable
{
    private readonly string _rootPath = Path.GetFullPath(rootPath);
    private KnowledgeDocument[] _documents = [];
    private readonly SemaphoreSlim _initializationLock = new(1, 1);

    public KnowledgeStatistics Statistics { get; private set; } = new(0, 0);

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_documents.Length > 0) return;
        await _initializationLock.WaitAsync(cancellationToken);
        try
        {
            if (_documents.Length > 0) return;
            if (!Directory.Exists(_rootPath)) throw new DirectoryNotFoundException($"知识目录不存在：{_rootPath}");

            var documents = new ConcurrentBag<KnowledgeDocument>();
            await Parallel.ForEachAsync(Directory.EnumerateFiles(_rootPath, "*.md", SearchOption.AllDirectories), cancellationToken,
                async (path, token) =>
                {
                    var text = await File.ReadAllTextAsync(path, token);
                    var document = Parse(path, text);
                    if (document is { Status: "published" }) documents.Add(document);
                });

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

    public void Dispose()
    {
        _initializationLock.Dispose();
    }

    private static KnowledgeDocument? Parse(string path, string text)
    {
        var frontMatter = Regex.Match(text, @"\A---\s*\r?\n(?<yaml>.*?)\r?\n---\s*\r?\n(?<body>.*)\z", RegexOptions.Singleline);
        if (!frontMatter.Success) return null;
        var metadata = ParseMetadata(frontMatter.Groups["yaml"].Value);
        if (!metadata.TryGetValue("synthetic", out var synthetic) || !string.Equals(synthetic, "true", StringComparison.OrdinalIgnoreCase)) return null;

        var id = Required("id");
        var version = Required("version").Trim('"', '\'');
        var title = Required("title");
        var tenant = Required("tenant_id");
        var groups = ParseList(metadata.GetValueOrDefault("acl_allow_groups"));
        var chunks = SplitIntoChunks(id, version, title, tenant, groups, path, frontMatter.Groups["body"].Value);
        return new KnowledgeDocument(id, version, title, tenant, groups, metadata.GetValueOrDefault("status", "draft"), path, chunks);

        string Required(string key) => metadata.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value : throw new InvalidDataException($"{path} 缺少必需元数据 {key}。");
    }

    private static Dictionary<string, string> ParseMetadata(string yaml) => yaml.Split('\n')
        .Select(line => line.Trim())
        .Where(line => line.Length > 0 && !line.StartsWith('#'))
        .Select(line => line.Split(':', 2))
        .Where(parts => parts.Length == 2)
        .ToDictionary(parts => parts[0].Trim(), parts => parts[1].Trim(), StringComparer.OrdinalIgnoreCase);

    private static HashSet<string> ParseList(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return new HashSet<string>(value.Trim('[', ']').Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => x.Trim('"', '\'')), StringComparer.OrdinalIgnoreCase);
    }

    private static List<KnowledgeChunk> SplitIntoChunks(string documentId, string version, string title, string tenant,
        IReadOnlySet<string> groups, string path, string body)
    {
        var chunks = new List<KnowledgeChunk>();
        var section = title;
        var buffer = new StringBuilder();
        var index = 0;

        void Flush()
        {
            var content = buffer.ToString().Trim();
            buffer.Clear();
            if (content.Length == 0) return;
            chunks.Add(new KnowledgeChunk($"{documentId}:{index++}", documentId, version, title, section, content, tenant, groups, path));
        }

        foreach (var line in body.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                Flush();
                section = line[3..].Trim();
            }
            else if (!line.StartsWith("# ", StringComparison.Ordinal))
            {
                buffer.AppendLine(line);
            }
        }
        Flush();
        return chunks;
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
