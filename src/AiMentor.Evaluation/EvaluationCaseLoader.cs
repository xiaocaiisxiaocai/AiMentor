using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using AiMentor.Domain;

namespace AiMentor.Evaluation;

public static partial class EvaluationCaseLoader
{
    private static readonly HashSet<string> AllowedCategories = new(StringComparer.Ordinal)
    {
        "factual", "procedural", "incident", "architecture", "no_answer",
        "conflict", "acl", "security", "pii", "memory"
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static async Task<IReadOnlyList<EvaluationCase>> LoadAsync(string casesPath, string suitePath,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(casesPath))
            throw new EvaluationDataException("EVAL_CASES_FILE_NOT_FOUND", $"评测题集不存在：{casesPath}");
        if (!File.Exists(suitePath))
            throw new EvaluationDataException("EVAL_SUITE_FILE_NOT_FOUND", $"评测套件配置不存在：{suitePath}");

        var suiteJson = await File.ReadAllTextAsync(suitePath, cancellationToken);
        var suite = ParseSuite(suiteJson);
        await using (var stream = File.OpenRead(casesPath))
        {
            var actualHash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
            if (!string.Equals(actualHash, suite.CasesSha256, StringComparison.OrdinalIgnoreCase))
                throw new EvaluationDataException("EVAL_CASES_HASH_MISMATCH",
                    "评测题集内容与套件 cases_sha256 不一致，必须显式评审并更新清单。");
        }
        var lines = await File.ReadAllLinesAsync(casesPath, cancellationToken);
        return ParseCases(lines, suite);
    }

    public static EvaluationSuiteDefinition ParseSuite(string json)
    {
        EvaluationSuiteDto dto;
        try
        {
            dto = JsonSerializer.Deserialize<EvaluationSuiteDto>(json, JsonOptions)
                  ?? throw new EvaluationDataException("EVAL_SUITE_EMPTY", "评测套件配置为空。");
        }
        catch (JsonException exception)
        {
            throw new EvaluationDataException("EVAL_SUITE_INVALID_JSON", $"评测套件配置不是有效 JSON：{exception.Message}");
        }

        if (dto.SchemaVersion is not (1 or 2))
            throw new EvaluationDataException("EVAL_SUITE_SCHEMA_UNSUPPORTED", "评测套件 schema_version 必须为 1 或 2。");
        var defaultGroups = NormalizeGroups(dto.DefaultGroups, "default_groups");
        var overrides = new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (caseId, groups) in dto.CaseGroupOverrides ?? [])
        {
            if (string.IsNullOrWhiteSpace(caseId))
                throw new EvaluationDataException("EVAL_SUITE_CASE_ID_INVALID", "case_group_overrides 包含空 CaseId。");
            overrides.Add(caseId.Trim(), NormalizeGroups(groups, $"case_group_overrides.{caseId}"));
        }

        if (dto.ExpectedCaseCount is null or <= 0)
            throw new EvaluationDataException("EVAL_SUITE_CASE_COUNT_INVALID", "expected_case_count 必须大于 0。");
        if (string.IsNullOrWhiteSpace(dto.CasesSha256) || !Sha256Regex().IsMatch(dto.CasesSha256))
            throw new EvaluationDataException("EVAL_SUITE_HASH_INVALID", "cases_sha256 必须是 64 位十六进制 SHA-256。");
        var categoryCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (category, count) in dto.ExpectedCategoryCounts ?? [])
        {
            if (!AllowedCategories.Contains(category) || count <= 0)
                throw new EvaluationDataException("EVAL_SUITE_CATEGORY_COUNT_INVALID",
                    $"expected_category_counts 包含无效项：{category}={count}。");
            categoryCounts.Add(category, count);
        }
        if (categoryCounts.Count == 0 || categoryCounts.Values.Sum() != dto.ExpectedCaseCount.Value)
            throw new EvaluationDataException("EVAL_SUITE_CATEGORY_COUNT_INVALID",
                "expected_category_counts 必须完整且总和等于 expected_case_count。");

        return new EvaluationSuiteDefinition(dto.SchemaVersion.Value, dto.CasesSha256.ToUpperInvariant(), dto.ExpectedCaseCount.Value,
            categoryCounts, defaultGroups, overrides);
    }

    public static IReadOnlyList<EvaluationCase> ParseCases(IEnumerable<string> lines, EvaluationSuiteDefinition suite)
    {
        var cases = new List<EvaluationCase>();
        var ids = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var lineNumber = 0;
        foreach (var rawLine in lines)
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(rawLine)) continue;

            EvaluationCaseDto dto;
            JsonElement caseElement;
            try
            {
                using var document = JsonDocument.Parse(rawLine);
                caseElement = document.RootElement.Clone();
                dto = JsonSerializer.Deserialize<EvaluationCaseDto>(rawLine, JsonOptions)
                      ?? throw Invalid(lineNumber, "EVAL_CASE_EMPTY", "题目为空。");
            }
            catch (JsonException exception)
            {
                throw Invalid(lineNumber, "EVAL_CASE_INVALID_JSON", exception.Message);
            }

            var caseId = Required(dto.CaseId, lineNumber, "case_id");
            if (ids.TryGetValue(caseId, out var firstLine))
                throw Invalid(lineNumber, "EVAL_CASE_ID_DUPLICATE", $"CaseId {caseId} 已在第 {firstLine} 行出现。");
            ids.Add(caseId, lineNumber);

            if (dto.SchemaVersion is not (1 or 2))
                throw Invalid(lineNumber, "EVAL_CASE_SCHEMA_UNSUPPORTED", "schema_version 必须为 1 或 2。");
            if (dto.SchemaVersion != suite.SchemaVersion)
            {
                // v1 保留既有错误码，避免测量基线因引入 v2 而产生无关变化。
                var code = suite.SchemaVersion == 1 ? "EVAL_CASE_SCHEMA_UNSUPPORTED" : "EVAL_CASE_SCHEMA_MISMATCH";
                throw Invalid(lineNumber, code,
                    $"题目 schema_version {dto.SchemaVersion} 与套件版本 {suite.SchemaVersion} 不一致。");
            }
            if (dto.Synthetic is not true)
                throw Invalid(lineNumber, "EVAL_CASE_SYNTHETIC_REQUIRED", "当前套件只允许 synthetic=true 的隔离题集。");

            var category = Required(dto.Category, lineNumber, "category").ToLowerInvariant();
            if (!AllowedCategories.Contains(category))
                throw Invalid(lineNumber, "EVAL_CASE_CATEGORY_UNKNOWN", $"未知 category：{category}。");
            var expectedAction = ParseExpectedAction(Required(dto.ExpectedAction, lineNumber, "expected_action"), lineNumber);
            ValidateCategoryAction(category, expectedAction, lineNumber);
            var risk = ParseRisk(Required(dto.RiskLevel, lineNumber, "risk_level"), lineNumber);
            ValidateMinimumRisk(category, risk, lineNumber);
            var groups = ResolveGroups(caseId, category, suite, lineNumber);
            var tenantId = Required(dto.TenantId, lineNumber, "tenant_id");
            var input = Required(dto.Input, lineNumber, "input");
            if (suite.SchemaVersion == 1)
            {
                if (caseElement.TryGetProperty("oracle", out _))
                    throw Invalid(lineNumber, "EVAL_CASE_ORACLE_FORBIDDEN", "v1 题目不得声明 oracle。");
                var evidence = Required(dto.Evidence, lineNumber, "evidence");
                cases.Add(new EvaluationCase(1, caseId, category, tenantId, $"evaluation:{caseId}", groups,
                    input, Required(dto.ExpectedBehavior, lineNumber, "expected_behavior"), evidence,
                    expectedAction, risk, dto.Synthetic.Value, ParseExpectedCitations(evidence)));
            }
            else
            {
                if (caseElement.TryGetProperty("expected_behavior", out _)
                    || caseElement.TryGetProperty("evidence", out _))
                    throw Invalid(lineNumber, "EVAL_CASE_LEGACY_FIELD_FORBIDDEN",
                        "v2 题目不得声明 expected_behavior 或 evidence。");
                var oracle = ParseOracle(dto.Oracle, caseElement, lineNumber);
                cases.Add(new EvaluationCase(2, caseId, category, tenantId, $"evaluation:{caseId}", groups,
                    input, null, null, expectedAction, risk, dto.Synthetic.Value,
                    oracle.RequiredCitations, oracle));
            }
        }

        if (cases.Count == 0)
            throw new EvaluationDataException("EVAL_CASES_EMPTY", "评测题集至少需要一条题目。");
        if (cases.Count != suite.ExpectedCaseCount)
            throw new EvaluationDataException("EVAL_CASE_COUNT_MISMATCH",
                $"题集应包含 {suite.ExpectedCaseCount} 条，实际 {cases.Count} 条。");
        var actualCategoryCounts = cases.GroupBy(item => item.Category, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        if (suite.ExpectedCategoryCounts.Any(expected =>
                !actualCategoryCounts.TryGetValue(expected.Key, out var actual) || actual != expected.Value)
            || actualCategoryCounts.Keys.Any(category => !suite.ExpectedCategoryCounts.ContainsKey(category)))
            throw new EvaluationDataException("EVAL_CATEGORY_COUNT_MISMATCH", "题集类别分布与套件清单不一致。");

        foreach (var overrideId in suite.CaseGroupOverrides.Keys)
            if (!ids.ContainsKey(overrideId))
                throw new EvaluationDataException("EVAL_SUITE_OVERRIDE_UNKNOWN_CASE", $"主体覆盖引用了不存在的 CaseId：{overrideId}。");

        return cases;
    }

    private static EvaluationOracle ParseOracle(EvaluationOracleDto? dto, JsonElement caseElement, int lineNumber)
    {
        if (!caseElement.TryGetProperty("oracle", out var oracleElement) || dto is null)
            throw Invalid(lineNumber, "EVAL_CASE_ORACLE_REQUIRED", "v2 题目必须声明 oracle 对象。");

        var requiredFields = new[]
        {
            "allowed_decisions", "allowed_safety_outcomes", "allowed_terminal_codes", "required_claims",
            "forbidden_claims", "required_citations", "forbidden_citations", "required_trace",
            "forbidden_trace", "require_no_citations", "fixture_id"
        };
        if (oracleElement.ValueKind != JsonValueKind.Object
            || requiredFields.Any(field => !oracleElement.TryGetProperty(field, out _)))
            throw Invalid(lineNumber, "EVAL_CASE_ORACLE_FIELD_REQUIRED", "oracle 的所有字段都必须显式声明，包括 fixture_id=null。");

        var allowedDecisions = ParseSet(dto.AllowedDecisions, "allowed_decisions", lineNumber, ParseDecision, true);
        var terminalCodes = ParseLiteralSet(dto.AllowedTerminalCodes, "allowed_terminal_codes", lineNumber, true);
        var safetyOutcomes = ParseSafetyOutcomes(dto.AllowedSafetyOutcomes, lineNumber);
        var requiredClaims = ParseClaims(dto.RequiredClaims, "required_claims", lineNumber);
        var forbiddenClaims = ParseClaims(dto.ForbiddenClaims, "forbidden_claims", lineNumber);
        var claimIds = new HashSet<string>(StringComparer.Ordinal);
        if (requiredClaims.Concat(forbiddenClaims).Any(claim => !claimIds.Add(claim.ClaimId)))
            throw Invalid(lineNumber, "EVAL_CASE_CLAIM_ID_DUPLICATE", "required_claims 与 forbidden_claims 中的 claim_id 必须全局唯一。");

        var requiredCitations = ParseCitations(dto.RequiredCitations, "required_citations", lineNumber);
        var forbiddenCitations = ParseCitations(dto.ForbiddenCitations, "forbidden_citations", lineNumber);
        if (dto.RequireNoCitations is null)
            throw Invalid(lineNumber, "EVAL_CASE_ORACLE_FIELD_REQUIRED", "require_no_citations 必须是布尔值。");
        if (dto.RequireNoCitations.Value && requiredCitations.Count > 0)
            throw Invalid(lineNumber, "EVAL_CASE_CITATION_CONFLICT", "要求无引用时不得同时声明 required_citations。");
        if (requiredCitations.Any(required => forbiddenCitations.Any(forbidden => CitationEquals(required, forbidden))))
            throw Invalid(lineNumber, "EVAL_CASE_CITATION_CONFLICT", "同一引用不得同时为 required 和 forbidden。");

        var requiredTrace = ParseLiteralList(dto.RequiredTrace, "required_trace", lineNumber);
        var forbiddenTrace = ParseLiteralSet(dto.ForbiddenTrace, "forbidden_trace", lineNumber, false);
        if (requiredTrace.Any(forbiddenTrace.Contains))
            throw Invalid(lineNumber, "EVAL_CASE_TRACE_CONFLICT", "同一轨迹事件不得同时为 required 和 forbidden。");

        var fixtureId = dto.FixtureId is null ? null : Required(dto.FixtureId, lineNumber, "oracle.fixture_id");
        return new EvaluationOracle(allowedDecisions, safetyOutcomes, terminalCodes, requiredClaims, forbiddenClaims,
            requiredCitations, forbiddenCitations, requiredTrace, forbiddenTrace, dto.RequireNoCitations.Value, fixtureId);
    }

    private static List<SafetyOutcomeOracle> ParseSafetyOutcomes(
        IReadOnlyList<SafetyOutcomeOracleDto?>? values, int lineNumber)
    {
        if (values is null || values.Count == 0)
            throw Invalid(lineNumber, "EVAL_CASE_ORACLE_VALUE_REQUIRED", "allowed_safety_outcomes 至少需要一个结果。");
        var actions = new HashSet<SafetyAction>();
        var result = new List<SafetyOutcomeOracle>();
        foreach (var value in values)
        {
            if (value is null)
                throw Invalid(lineNumber, "EVAL_CASE_ORACLE_VALUE_REQUIRED", "allowed_safety_outcomes 不得包含 null。");
            var action = ParseSafetyAction(Required(value.Action, lineNumber, "oracle.allowed_safety_outcomes.action"), lineNumber);
            if (!actions.Add(action))
                throw Invalid(lineNumber, "EVAL_CASE_SAFETY_OUTCOME_DUPLICATE", $"安全动作 {action} 重复声明。");
            result.Add(new SafetyOutcomeOracle(action,
                ParseLiteralSet(value.Codes, "allowed_safety_outcomes.codes", lineNumber, true)));
        }
        return result;
    }

    private static List<ClaimOracle> ParseClaims(IReadOnlyList<ClaimOracleDto?>? values, string field,
        int lineNumber)
    {
        if (values is null)
            throw Invalid(lineNumber, "EVAL_CASE_ORACLE_FIELD_REQUIRED", $"{field} 必须声明为数组。");
        var result = new List<ClaimOracle>();
        foreach (var value in values)
        {
            if (value is null || value.AcceptedAlternatives is null || value.AcceptedAlternatives.Count == 0)
                throw Invalid(lineNumber, "EVAL_CASE_CLAIM_ALTERNATIVE_INVALID", $"{field} 的每个 claim 至少需要一个文本替代项。");
            var alternatives = new List<IReadOnlyList<string>>();
            foreach (var alternative in value.AcceptedAlternatives)
            {
                if (alternative is null || alternative.Count == 0)
                    throw Invalid(lineNumber, "EVAL_CASE_CLAIM_ALTERNATIVE_INVALID", $"{field} 不得包含空替代项。");
                alternatives.Add(ParseLiteralList(alternative, $"{field}.accepted_alternatives", lineNumber));
            }
            result.Add(new ClaimOracle(Required(value.ClaimId, lineNumber, $"oracle.{field}.claim_id"), alternatives));
        }
        return result;
    }

    private static List<ExpectedCitation> ParseCitations(IReadOnlyList<CitationOracleDto?>? values,
        string field, int lineNumber)
    {
        if (values is null)
            throw Invalid(lineNumber, "EVAL_CASE_ORACLE_FIELD_REQUIRED", $"{field} 必须声明为数组。");
        var result = new List<ExpectedCitation>();
        foreach (var value in values)
        {
            if (value is null)
                throw Invalid(lineNumber, "EVAL_CASE_CITATION_INVALID", $"{field} 不得包含 null。");
            var citation = new ExpectedCitation(Required(value.DocumentId, lineNumber, $"oracle.{field}.document_id"),
                value.Version is null ? null : Required(value.Version, lineNumber, $"oracle.{field}.version"));
            if (result.Any(existing => CitationEquals(existing, citation)))
                throw Invalid(lineNumber, "EVAL_CASE_CITATION_DUPLICATE", $"{field} 包含重复引用。");
            result.Add(citation);
        }
        return result;
    }

    private static HashSet<T> ParseSet<T>(IReadOnlyList<string>? values, string field, int lineNumber,
        Func<string, int, T> parser, bool requireNonEmpty) where T : notnull
    {
        if (values is null || requireNonEmpty && values.Count == 0)
            throw Invalid(lineNumber, "EVAL_CASE_ORACLE_VALUE_REQUIRED", $"{field} 至少需要一个值。");
        var result = new HashSet<T>();
        foreach (var value in values)
            if (!result.Add(parser(Required(value, lineNumber, $"oracle.{field}"), lineNumber)))
                throw Invalid(lineNumber, "EVAL_CASE_ORACLE_VALUE_DUPLICATE", $"{field} 包含重复值。");
        return result;
    }

    private static HashSet<string> ParseLiteralSet(IReadOnlyList<string>? values, string field, int lineNumber,
        bool requireNonEmpty) => ParseSet(values, field, lineNumber, (value, _) => value, requireNonEmpty);

    private static List<string> ParseLiteralList(IReadOnlyList<string>? values, string field, int lineNumber)
    {
        if (values is null)
            throw Invalid(lineNumber, "EVAL_CASE_ORACLE_FIELD_REQUIRED", $"{field} 必须声明为数组。");
        var result = new List<string>();
        var unique = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            var literal = Required(value, lineNumber, $"oracle.{field}");
            if (!unique.Add(literal))
                throw Invalid(lineNumber, "EVAL_CASE_ORACLE_VALUE_DUPLICATE", $"{field} 包含重复值。");
            result.Add(literal);
        }
        return result;
    }

    private static bool CitationEquals(ExpectedCitation left, ExpectedCitation right) =>
        string.Equals(left.DocumentId, right.DocumentId, StringComparison.Ordinal)
        && string.Equals(left.Version, right.Version, StringComparison.Ordinal);

    private static IReadOnlySet<string> ResolveGroups(string caseId, string category, EvaluationSuiteDefinition suite,
        int lineNumber)
    {
        if (suite.CaseGroupOverrides.TryGetValue(caseId, out var groups)) return groups;
        // ACL 题必须在提示词之外声明调用者；从问题文字猜身份会重现旧版“全权限假绿”。
        if (category == "acl")
            throw Invalid(lineNumber, "EVAL_ACL_ACTOR_REQUIRED", "ACL 题必须在套件配置中显式声明主体组。");
        return suite.DefaultGroups;
    }

    private static ExpectedCitation[] ParseExpectedCitations(string evidence)
    {
        var targets = new List<ExpectedCitation>();
        var targetKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match match in DocumentReferenceRegex().Matches(evidence))
        {
            var prefix = $"BK-{match.Groups["family"].Value}-";
            Add(prefix + match.Groups["number"].Value, VersionAfter(evidence, match.Index + match.Length));
            foreach (Match continuation in ContinuationRegex().Matches(match.Groups["continuations"].Value))
                Add(prefix + continuation.Groups["number"].Value, null);
        }
        return targets.ToArray();

        void Add(string documentId, string? version)
        {
            var key = $"{documentId}\u001f{version}";
            if (targetKeys.Add(key)) targets.Add(new ExpectedCitation(documentId, version));
        }
    }

    private static string? VersionAfter(string evidence, int start)
    {
        var match = VersionRegex().Match(evidence, start);
        return match.Success && match.Index == start ? match.Groups["version"].Value : null;
    }

    private static ExpectedAction ParseExpectedAction(string value, int lineNumber) => value switch
    {
        "answer" => ExpectedAction.Answer,
        "workflow" => ExpectedAction.Workflow,
        "clarify_or_refuse" => ExpectedAction.ClarifyOrRefuse,
        "resolve_or_escalate" => ExpectedAction.ResolveOrEscalate,
        "policy_decision" => ExpectedAction.PolicyDecision,
        "memory_policy" => ExpectedAction.MemoryPolicy,
        _ => throw Invalid(lineNumber, "EVAL_CASE_ACTION_UNKNOWN", $"未知 expected_action：{value}。")
    };

    private static AnswerDecision ParseDecision(string value, int lineNumber) => value switch
    {
        "answered" => AnswerDecision.Answered,
        "refused" => AnswerDecision.Refused,
        "insufficient_evidence" => AnswerDecision.InsufficientEvidence,
        "failed" => AnswerDecision.Failed,
        _ => throw Invalid(lineNumber, "EVAL_CASE_DECISION_UNKNOWN", $"未知 allowed_decisions 值：{value}。")
    };

    private static SafetyAction ParseSafetyAction(string value, int lineNumber) => value switch
    {
        "allow" => SafetyAction.Allow,
        "transform" => SafetyAction.Transform,
        "refuse" => SafetyAction.Refuse,
        "require_approval" => SafetyAction.RequireApproval,
        _ => throw Invalid(lineNumber, "EVAL_CASE_SAFETY_ACTION_UNKNOWN", $"未知安全动作：{value}。")
    };

    private static EvaluationRiskLevel ParseRisk(string value, int lineNumber) => value switch
    {
        "low" => EvaluationRiskLevel.Low,
        "medium" => EvaluationRiskLevel.Medium,
        "high" => EvaluationRiskLevel.High,
        "critical" => EvaluationRiskLevel.Critical,
        _ => throw Invalid(lineNumber, "EVAL_CASE_RISK_UNKNOWN", $"未知 risk_level：{value}。")
    };

    private static void ValidateMinimumRisk(string category, EvaluationRiskLevel risk, int lineNumber)
    {
        var minimum = category switch
        {
            "factual" => EvaluationRiskLevel.Low,
            "procedural" or "architecture" or "no_answer" => EvaluationRiskLevel.Medium,
            "incident" or "conflict" or "memory" => EvaluationRiskLevel.High,
            "acl" or "security" or "pii" => EvaluationRiskLevel.Critical,
            _ => throw Invalid(lineNumber, "EVAL_CASE_CATEGORY_UNKNOWN", $"未知 category：{category}。")
        };
        if (risk < minimum)
            throw Invalid(lineNumber, "EVAL_CASE_RISK_BELOW_MINIMUM", $"category {category} 的风险不得低于 {minimum}。");
    }

    private static void ValidateCategoryAction(string category, ExpectedAction action, int lineNumber)
    {
        var expected = category switch
        {
            "factual" or "architecture" => ExpectedAction.Answer,
            "procedural" or "incident" => ExpectedAction.Workflow,
            "no_answer" => ExpectedAction.ClarifyOrRefuse,
            "conflict" => ExpectedAction.ResolveOrEscalate,
            "acl" or "security" or "pii" => ExpectedAction.PolicyDecision,
            "memory" => ExpectedAction.MemoryPolicy,
            _ => throw Invalid(lineNumber, "EVAL_CASE_CATEGORY_UNKNOWN", $"未知 category：{category}。")
        };
        if (action != expected)
            throw Invalid(lineNumber, "EVAL_CASE_CATEGORY_ACTION_MISMATCH",
                $"category {category} 必须使用 expected_action {ToWireValue(expected)}。");
    }

    private static string ToWireValue(ExpectedAction action) => action switch
    {
        ExpectedAction.Answer => "answer",
        ExpectedAction.Workflow => "workflow",
        ExpectedAction.ClarifyOrRefuse => "clarify_or_refuse",
        ExpectedAction.ResolveOrEscalate => "resolve_or_escalate",
        ExpectedAction.PolicyDecision => "policy_decision",
        ExpectedAction.MemoryPolicy => "memory_policy",
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, null)
    };

    private static string Required(string? value, int lineNumber, string field) =>
        !string.IsNullOrWhiteSpace(value) ? value.Trim() : throw Invalid(lineNumber, "EVAL_CASE_FIELD_REQUIRED", $"缺少必填字段 {field}。");

    private static HashSet<string> NormalizeGroups(IEnumerable<string>? groups, string field)
    {
        var result = new HashSet<string>((groups ?? []).Where(group => !string.IsNullOrWhiteSpace(group))
            .Select(group => group.Trim()), StringComparer.OrdinalIgnoreCase);
        if (result.Count == 0)
            throw new EvaluationDataException("EVAL_SUITE_GROUPS_REQUIRED", $"{field} 至少需要一个非空主体组。");
        return result;
    }

    private static EvaluationDataException Invalid(int lineNumber, string code, string detail) =>
        new(code, $"评测题集第 {lineNumber} 行无效：{detail}");

    [GeneratedRegex(@"BK-(?<family>[A-Z]+)-(?<number>\d{3})(?<continuations>(?:[、,，]\d{3})*)", RegexOptions.CultureInvariant)]
    private static partial Regex DocumentReferenceRegex();

    [GeneratedRegex(@"[、,，](?<number>\d{3})", RegexOptions.CultureInvariant)]
    private static partial Regex ContinuationRegex();

    [GeneratedRegex(@"\s+v(?<version>\d+(?:\.\d+)*)", RegexOptions.CultureInvariant)]
    private static partial Regex VersionRegex();

    [GeneratedRegex(@"\A[A-Fa-f0-9]{64}\z", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Regex();

    private sealed class EvaluationCaseDto
    {
        [JsonPropertyName("schema_version")] public int? SchemaVersion { get; init; }
        [JsonPropertyName("case_id")] public string? CaseId { get; init; }
        public string? Category { get; init; }
        [JsonPropertyName("tenant_id")] public string? TenantId { get; init; }
        public string? Input { get; init; }
        [JsonPropertyName("expected_behavior")] public string? ExpectedBehavior { get; init; }
        public string? Evidence { get; init; }
        [JsonPropertyName("expected_action")] public string? ExpectedAction { get; init; }
        [JsonPropertyName("risk_level")] public string? RiskLevel { get; init; }
        public bool? Synthetic { get; init; }
        public EvaluationOracleDto? Oracle { get; init; }
    }

    private sealed class EvaluationOracleDto
    {
        [JsonPropertyName("allowed_decisions")] public List<string>? AllowedDecisions { get; init; }
        [JsonPropertyName("allowed_safety_outcomes")] public List<SafetyOutcomeOracleDto?>? AllowedSafetyOutcomes { get; init; }
        [JsonPropertyName("allowed_terminal_codes")] public List<string>? AllowedTerminalCodes { get; init; }
        [JsonPropertyName("required_claims")] public List<ClaimOracleDto?>? RequiredClaims { get; init; }
        [JsonPropertyName("forbidden_claims")] public List<ClaimOracleDto?>? ForbiddenClaims { get; init; }
        [JsonPropertyName("required_citations")] public List<CitationOracleDto?>? RequiredCitations { get; init; }
        [JsonPropertyName("forbidden_citations")] public List<CitationOracleDto?>? ForbiddenCitations { get; init; }
        [JsonPropertyName("required_trace")] public List<string>? RequiredTrace { get; init; }
        [JsonPropertyName("forbidden_trace")] public List<string>? ForbiddenTrace { get; init; }
        [JsonPropertyName("require_no_citations")] public bool? RequireNoCitations { get; init; }
        [JsonPropertyName("fixture_id")] public string? FixtureId { get; init; }
    }

    private sealed class SafetyOutcomeOracleDto
    {
        public string? Action { get; init; }
        public List<string>? Codes { get; init; }
    }

    private sealed class ClaimOracleDto
    {
        [JsonPropertyName("claim_id")] public string? ClaimId { get; init; }
        [JsonPropertyName("accepted_alternatives")] public List<List<string>?>? AcceptedAlternatives { get; init; }
    }

    private sealed class CitationOracleDto
    {
        [JsonPropertyName("document_id")] public string? DocumentId { get; init; }
        public string? Version { get; init; }
    }

    private sealed class EvaluationSuiteDto
    {
        [JsonPropertyName("schema_version")] public int? SchemaVersion { get; init; }
        [JsonPropertyName("cases_sha256")] public string? CasesSha256 { get; init; }
        [JsonPropertyName("expected_case_count")] public int? ExpectedCaseCount { get; init; }
        [JsonPropertyName("expected_category_counts")] public Dictionary<string, int>? ExpectedCategoryCounts { get; init; }
        [JsonPropertyName("default_groups")] public string[]? DefaultGroups { get; init; }
        [JsonPropertyName("case_group_overrides")] public Dictionary<string, string[]>? CaseGroupOverrides { get; init; }
    }
}

public sealed record EvaluationSuiteDefinition(
    int SchemaVersion,
    string CasesSha256,
    int ExpectedCaseCount,
    IReadOnlyDictionary<string, int> ExpectedCategoryCounts,
    IReadOnlySet<string> DefaultGroups,
    IReadOnlyDictionary<string, IReadOnlySet<string>> CaseGroupOverrides);
