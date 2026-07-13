using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using AiMentor.Application;
using AiMentor.Domain;

namespace AiMentor.Infrastructure;

public static class SafetyPolicyVersions
{
    public const string Current = "2026-07-13.2";
}

public sealed partial class RuleBasedRetrievedContentSafetyService : IRetrievedContentSafetyService
{
    public string PolicyVersion => SafetyPolicyVersions.Current;

    public RetrievedContentReview Review(IReadOnlyList<Evidence> evidence)
    {
        var accepted = new List<Evidence>(evidence.Count);
        var rejected = new List<RetrievedContentRejection>();
        foreach (var item in evidence)
        {
            var code = EmbeddedSecret().IsMatch(item.Chunk.Content)
                ? "RETRIEVED_SECRET_VALUE"
                : EmbeddedInstruction().IsMatch(item.Chunk.Content)
                    ? "RETRIEVED_PROMPT_INJECTION"
                    : null;
            if (code is null) accepted.Add(item);
            else rejected.Add(new RetrievedContentRejection(item.Chunk.Id, code));
        }

        return new RetrievedContentReview(accepted, rejected);
    }

    [GeneratedRegex(@"-----BEGIN (?:RSA |EC )?PRIVATE KEY-----|\bAKIA[0-9A-Z]{16}\b|\b(?:sk|pk)_(?:live|test)_[a-z0-9]{12,}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EmbeddedSecret();

    [GeneratedRegex(@"(?is)<\s*(?:system|developer|assistant)\b|BEGIN\s+(?:SYSTEM|DEVELOPER)\s+(?:PROMPT|MESSAGE)|(?:忽略|无视|绕过).{0,40}(?:系统|开发者|之前|以上).{0,30}(?:指令|规则)|ignore.{0,40}(?:system|developer|previous).{0,30}(?:instruction|message)", RegexOptions.CultureInvariant)]
    private static partial Regex EmbeddedInstruction();
}

public sealed class ToolSafetyOptions
{
    public IReadOnlySet<string> AllowedTools { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyDictionary<string, IReadOnlySet<string>> AllowedNetworkHosts { get; init; } =
        new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase);
}

public sealed partial class RuleBasedToolInvocationSafetyService(ToolSafetyOptions options) : IToolInvocationSafetyService
{
    public string PolicyVersion => SafetyPolicyVersions.Current;

    public SafetyDecision Review(ToolInvocationRequest request, AccessContext access)
    {
        if (string.IsNullOrWhiteSpace(request.ToolName) || !options.AllowedTools.Contains(request.ToolName))
            return Refuse("TOOL_NOT_ALLOWED", "工具不在当前策略允许清单中，已阻止调用。");

        JsonElement serializedArguments;
        try
        {
            serializedArguments = JsonSerializer.SerializeToElement(request.Arguments);
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            return Refuse("TOOL_ARGUMENTS_UNINSPECTABLE", "工具参数无法完成结构化安全检查，已阻止调用。");
        }

        var properties = EnumerateProperties(serializedArguments).ToArray();
        if (properties.Any(property => SensitiveArgumentName().IsMatch(property.Name)))
            return Refuse("SENSITIVE_TOOL_ARGUMENT", "工具参数包含敏感凭证字段，禁止传递。");

        var tenantArguments = properties.Where(property =>
            property.Name.Equals("tenantId", StringComparison.OrdinalIgnoreCase)
            || property.Name.Equals("tenant_id", StringComparison.OrdinalIgnoreCase));
        if (tenantArguments.Any(property =>
            !string.Equals(PropertyValue(property.Value), access.TenantId, StringComparison.OrdinalIgnoreCase)))
            return Refuse("CROSS_TENANT_TOOL_ARGUMENT", "工具参数中的租户与当前授权上下文不一致。");

        foreach (var property in properties.Where(property => NetworkTargetName().IsMatch(property.Name)))
        {
            var targetDecision = ReviewNetworkTarget(request.ToolName, PropertyValue(property.Value));
            if (targetDecision.Action != SafetyAction.Allow) return targetDecision;
        }

        if (request.Risk != ToolOperationRisk.ReadOnly)
            return new SafetyDecision(SafetyAction.RequireApproval, "TOOL_OPERATION_REQUIRES_APPROVAL", "修改性或特权工具操作必须获得显式批准。");

        return new SafetyDecision(SafetyAction.Allow, "TOOL_ARGUMENTS_SAFE", "工具名称、风险等级和参数通过策略审核。");
    }

    private static SafetyDecision Refuse(string code, string message) => new(SafetyAction.Refuse, code, message);

    private static IEnumerable<JsonProperty> EnumerateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                yield return property;
                foreach (var nested in EnumerateProperties(property.Value)) yield return nested;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                foreach (var nested in EnumerateProperties(item)) yield return nested;
        }
    }

    private static string? PropertyValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Null => null,
        _ => Convert.ToString(value.GetRawText(), CultureInfo.InvariantCulture)
    };

    private SafetyDecision ReviewNetworkTarget(string toolName, string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
            || !string.IsNullOrEmpty(uri.UserInfo) || IsPrivateHost(uri.Host))
            return Refuse("TOOL_NETWORK_TARGET_INVALID", "网络目标必须是无内嵌凭证的公网 HTTPS 地址。");
        if (!options.AllowedNetworkHosts.TryGetValue(toolName, out var allowedHosts)
            || !allowedHosts.Contains(uri.IdnHost))
            return Refuse("TOOL_NETWORK_TARGET_NOT_ALLOWED", "网络目标不在该工具的服务器端允许清单中。");
        return new SafetyDecision(SafetyAction.Allow, "TOOL_NETWORK_TARGET_SAFE", "网络目标通过策略审核。");
    }

    private static bool IsPrivateHost(string host)
    {
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true;
        if (!IPAddress.TryParse(host, out var address)) return false;
        if (IPAddress.IsLoopback(address) || address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast)
            return true;
        if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return false;
        var bytes = address.GetAddressBytes();
        return bytes[0] is 0 or 10 or 127
            || bytes[0] == 169 && bytes[1] == 254
            || bytes[0] == 172 && bytes[1] is >= 16 and <= 31
            || bytes[0] == 192 && bytes[1] == 168
            || bytes[0] >= 224;
    }

    [GeneratedRegex(@"password|passwd|token|secret|api[_-]?key|private[_-]?key|authorization", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SensitiveArgumentName();

    [GeneratedRegex(@"(?:url|uri|endpoint|callback|webhook)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NetworkTargetName();
}

public sealed partial class RuleBasedOutputSafetyService : IOutputSafetyService
{
    public string PolicyVersion => SafetyPolicyVersions.Current;

    public SafetyDecision Review(string answer, IReadOnlyList<Evidence> evidence, IReadOnlyList<Citation> citations)
    {
        if (string.IsNullOrWhiteSpace(answer)) return Refuse("OUTPUT_EMPTY", "模型没有生成可审核的回答。");
        if (SecretValue().IsMatch(answer)) return Refuse("OUTPUT_SECRET_LEAK", "模型输出疑似包含可用凭证，已阻止返回。");
        if (PolicyBypassClaim().IsMatch(answer)) return Refuse("OUTPUT_POLICY_BYPASS", "模型输出疑似服从了越权指令，已阻止返回。");
        if (citations.Count == 0) return Refuse("OUTPUT_WITHOUT_CITATION", "有证据回答必须至少包含一个可验证引用。");

        foreach (var citation in citations)
        {
            var source = evidence.FirstOrDefault(item =>
                item.Chunk.DocumentId == citation.DocumentId
                && item.Chunk.Version == citation.Version
                && item.Chunk.Section == citation.Section);
            if (source is null) return Refuse("CITATION_SOURCE_MISMATCH", "回答引用了本次证据集合之外的来源。");

            var normalizedContent = Normalize(source.Chunk.Content);
            if (string.IsNullOrWhiteSpace(citation.Quote) || !normalizedContent.Contains(Normalize(citation.Quote), StringComparison.Ordinal))
                return Refuse("CITATION_QUOTE_MISMATCH", "引用摘录无法在对应证据中验证。");
        }

        var groundingText = TrustedPresentationPrefix().Replace(answer, string.Empty);
        var answerTokens = EvidenceTextAnalysis.Tokens(groundingText);
        var evidenceTokens = EvidenceTextAnalysis.Tokens(string.Join(' ', evidence.Select(item => item.Chunk.Content)));
        var groundingCoverage = EvidenceTextAnalysis.Coverage(answerTokens, evidenceTokens);
        if (groundingCoverage < 0.15)
            return Refuse("OUTPUT_NOT_GROUNDED", "模型输出与可访问证据缺少足够的词项对应关系。");

        return new SafetyDecision(SafetyAction.Allow, "OUTPUT_SAFE", "模型输出与结构化引用通过安全和一致性审核。");
    }

    private static string Normalize(string value) =>
        string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static SafetyDecision Refuse(string code, string message) => new(SafetyAction.Refuse, code, message);

    [GeneratedRegex(@"-----BEGIN (?:RSA |EC )?PRIVATE KEY-----|\bAKIA[0-9A-Z]{16}\b|\b(?:sk|pk)_(?:live|test)_[a-z0-9]{12,}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SecretValue();

    [GeneratedRegex(@"(?:已|我已).{0,12}(?:忽略|绕过|无视).{0,12}(?:系统|安全|开发者).{0,12}(?:指令|规则)|(?:system|developer)\s+prompt\s*(?:is|如下)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PolicyBypassClaim();

    [GeneratedRegex(@"^\s*(?:根据当前可访问的正式知识|根据(?:当前)?证据)\s*[:：]?\s*", RegexOptions.CultureInvariant)]
    private static partial Regex TrustedPresentationPrefix();
}
