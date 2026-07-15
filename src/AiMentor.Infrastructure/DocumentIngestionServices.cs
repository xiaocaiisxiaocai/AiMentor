using System.Text;
using System.Text.RegularExpressions;
using AiMentor.Application;
using AiMentor.Domain;

namespace AiMentor.Infrastructure;

/// <summary>首期只路由原生 Markdown/TXT；未知格式必须明确失败，禁止把二进制误当文本发布。</summary>
public sealed class DocumentParserRouter(IDocumentParser textParser) : IDocumentParserRouter
{
    public IDocumentParser Resolve(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".md" or ".txt" => textParser,
        var extension => throw new NotSupportedException($"不支持的知识文档格式：{extension}。首期仅支持 .md 和 .txt。")
    };
}

/// <summary>解析带 YAML front matter 的 Markdown/TXT，并保留标题层级、阅读顺序和行号锚点。</summary>
public sealed partial class MarkdownDocumentParser : IDocumentParser
{
    public async Task<ParsedKnowledgeDocument> ParseAsync(string path, string sourcePath,
        CancellationToken cancellationToken = default)
    {
        var text = await File.ReadAllTextAsync(path, cancellationToken);
        var frontMatter = FrontMatter().Match(text);
        if (!frontMatter.Success) throw new InvalidDataException($"{path} 缺少有效 front matter，禁止发布。");

        var metadata = ParseMetadata(frontMatter.Groups["yaml"].Value);
        var id = Required("id");
        var version = Required("version").Trim('"', '\'');
        var title = Required("title");
        var tenant = Required("tenant_id");
        var status = Required("status");
        var groups = ParseList(metadata.GetValueOrDefault("acl_allow_groups"));
        var body = frontMatter.Groups["body"].Value.Replace("\r\n", "\n", StringComparison.Ordinal);
        var bodyStartLine = text[..frontMatter.Groups["body"].Index].Count(character => character == '\n') + 1;
        var elements = ParseElements(id, title, sourcePath, body, bodyStartLine);
        return new ParsedKnowledgeDocument(id, version, title, tenant, groups, status, sourcePath, elements);

        string Required(string key) => metadata.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value : throw new InvalidDataException($"{path} 缺少必需元数据 {key}，禁止发布。");
    }

    private static List<DocumentElement> ParseElements(string documentId, string title, string sourcePath,
        string body, int bodyStartLine)
    {
        var result = new List<DocumentElement>();
        var sections = new List<string> { title };
        var buffer = new StringBuilder();
        var startLine = bodyStartLine;
        var order = 0;
        string? parentId = null;
        var lines = body.Split('\n');

        void Flush(int endLine)
        {
            var content = buffer.ToString().Trim();
            buffer.Clear();
            if (content.Length == 0) return;
            var id = $"{documentId}:element:{order}";
            result.Add(new DocumentElement(id, "text", sections.ToArray(), content, order++,
                $"{sourcePath}#L{startLine}-L{Math.Max(startLine, endLine)}", parentId));
            parentId = id;
        }

        for (var index = 0; index < lines.Length; index++)
        {
            var lineNumber = bodyStartLine + index;
            var heading = Heading().Match(lines[index]);
            if (heading.Success)
            {
                Flush(lineNumber - 1);
                // front matter 标题是稳定根节点；正文 H1 作为可引用章节保留，不能移除根节点后生成空 Section。
                var level = Math.Max(2, heading.Groups["marks"].Value.Length);
                var name = heading.Groups["title"].Value.Trim();
                while (sections.Count >= level) sections.RemoveAt(sections.Count - 1);
                sections.Add(name);
                parentId = null;
                startLine = lineNumber + 1;
            }
            else
            {
                if (buffer.Length == 0) startLine = lineNumber;
                buffer.AppendLine(lines[index]);
            }
        }
        Flush(bodyStartLine + lines.Length - 1);
        return result;
    }

    private static Dictionary<string, string> ParseMetadata(string yaml) => yaml.Split('\n')
        .Select(line => line.Trim()).Where(line => line.Length > 0 && !line.StartsWith('#'))
        .Select(line => line.Split(':', 2)).Where(parts => parts.Length == 2)
        .ToDictionary(parts => parts[0].Trim(), parts => parts[1].Trim(), StringComparer.OrdinalIgnoreCase);

    private static HashSet<string> ParseList(string? value) => string.IsNullOrWhiteSpace(value)
        ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        : new HashSet<string>(value.Trim('[', ']').Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(item => item.Trim('"', '\'')), StringComparer.OrdinalIgnoreCase);

    [GeneratedRegex(@"\A---\s*\r?\n(?<yaml>.*?)\r?\n---\s*\r?\n(?<body>.*)\z", RegexOptions.Singleline)]
    private static partial Regex FrontMatter();

    [GeneratedRegex(@"^(?<marks>#{1,6})\s+(?<title>.+?)\s*$")]
    private static partial Regex Heading();
}

/// <summary>按解析器恢复的结构边界切分，并把原文锚点传递给检索单元的来源路径。</summary>
public sealed class StructuredDocumentChunker : IDocumentChunker
{
    public IReadOnlyList<KnowledgeChunk> Chunk(ParsedKnowledgeDocument document) => document.Elements
        .Where(element => !string.IsNullOrWhiteSpace(element.Text))
        .Select((element, index) => new KnowledgeChunk($"{document.Id}:{index}", document.Id, document.Version,
            document.Title, string.Join(" / ", element.SectionPath.Skip(1)), element.Text, document.TenantId,
            document.AllowedGroups, element.SourceAnchor!))
        .ToArray();
}

/// <summary>发布前验证非空正文、确定性阅读顺序和原文锚点；任一缺失都失败关闭。</summary>
public sealed class RuleBasedParseQualityGate : IParseQualityGate
{
    public ParseQualityDecision Evaluate(ParsedKnowledgeDocument document)
    {
        if (document.Elements.Count == 0 || document.Elements.All(element => string.IsNullOrWhiteSpace(element.Text)))
            return Reject("PARSE_EMPTY", "解析结果没有可发布正文。");
        if (document.Elements.Select(element => element.Id).Distinct(StringComparer.Ordinal).Count() != document.Elements.Count)
            return Reject("PARSE_DUPLICATE_ELEMENT", "解析元素标识不唯一。");
        if (!document.Elements.Select(element => element.ReadingOrder).SequenceEqual(Enumerable.Range(0, document.Elements.Count)))
            return Reject("PARSE_READING_ORDER_INVALID", "解析元素阅读顺序不连续。");
        if (document.Elements.Any(element => string.IsNullOrWhiteSpace(element.SourceAnchor)))
            return Reject("PARSE_SOURCE_ANCHOR_MISSING", "解析元素无法回查原文锚点。");
        return new ParseQualityDecision(true, "PARSE_QUALITY_PASSED", "解析结果通过发布质量门禁。");
    }

    private static ParseQualityDecision Reject(string code, string message) => new(false, code, message);
}
