using AiMentor.Application;

namespace AiMentor.Infrastructure;

/// <summary>SystemDoctor 的脱敏配置投影；只保存判断结果和非敏感标识。</summary>
public record SystemDoctorOptions
{
    public bool IsProduction { get; init; }
    public bool LegacyApiEnabled { get; init; }
    public string AuthenticationMode { get; init; } = "Development";
    public string? AuthenticationAuthority { get; init; }
    public bool AuthenticationRequireHttpsMetadata { get; init; } = true;
    public bool AuthenticationAudienceConfigured { get; init; }
    public bool SubjectClaimConfigured { get; init; }
    public bool TenantClaimConfigured { get; init; }
    public string WorkflowProvider { get; init; } = "InMemory";
    public bool AtlasIncidentStorePersistent { get; init; }
    public bool MemoryStorePersistent { get; init; }
    public bool MemoryKeyConfigured { get; init; }
    public bool MemoryKeyValid { get; init; } = true;
    public bool WorkflowKeyRingConfigured { get; init; }
    public bool WorkflowActiveKeyPresent { get; init; } = true;
    public bool WorkflowUsesIndependentKey { get; init; }
    public bool SqlConnectionConfigured { get; init; }
    public bool SqlEncrypt { get; init; }
    public bool SqlTrustServerCertificate { get; init; }
    public string RagProvider { get; init; } = "Local";
    public string? OpenSearchEndpoint { get; init; }
    public bool OpenSearchAuthenticationConfigured { get; init; }
    public string ModelProvider { get; init; } = "Deterministic";
    public string EmbeddingProvider { get; init; } = "Deterministic";
    public string RerankerProvider { get; init; } = "Lexical";
    public int EmbeddingDimensions { get; init; }
    public int ExpectedEmbeddingDimensions { get; init; }
    public string EmbeddingIndexVersion { get; init; } = string.Empty;
    public string ExpectedEmbeddingIndexVersion { get; init; } = string.Empty;
    public TimeSpan AgentMaximumRunTime { get; init; }
    public TimeSpan AgentResumeLeaseDuration { get; init; }
    public TimeSpan AgentResumeLeaseRenewalInterval { get; init; }
}

/// <summary>集中检查身份、密钥、存储、RAG 与 AI 供应商的安全配置，不连接或修改外部系统。</summary>
public sealed class SystemDoctor(SystemDoctorOptions options, TimeProvider timeProvider,
    MemoryRetentionHealthState? memoryRetentionHealth = null) : ISystemDoctor
{
    public Task<SystemDiagnosticReport> RunAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var checks = new List<SystemCheckResult>();
        CheckAuthentication(checks);
        CheckKeysAndWorkflow(checks);
        CheckRag(checks);
        CheckAiProviders(checks);
        CheckAgentLease(checks);
        var report = new SystemDiagnosticReport(checks.All(item => item.Status != SystemCheckStatus.Failed),
            timeProvider.GetUtcNow(), checks);
        return Task.FromResult(report);
    }

    private void CheckAuthentication(List<SystemCheckResult> checks)
    {
        var oidc = Equals(options.AuthenticationMode, "OidcJwt");
        var development = Equals(options.AuthenticationMode, "Development");
        if (!oidc && !development)
            AddFailure(checks, "authentication.mode", "AUTH_MODE_UNSUPPORTED", "身份模式不受支持。");
        else if (options.IsProduction && !oidc)
            AddFailure(checks, "authentication.mode", "AUTH_PRODUCTION_OIDC_REQUIRED", "生产环境必须启用 OIDC JWT。");
        else
            Add(checks, "authentication.mode", options.IsProduction ? SystemCheckSeverity.Critical : SystemCheckSeverity.Warning,
                development ? SystemCheckStatus.Warning : SystemCheckStatus.Passed,
                development ? "AUTH_DEVELOPMENT_MODE" : "AUTH_MODE_VALID",
                development ? "当前使用开发身份模式。" : "身份模式满足环境要求。");

        if (oidc)
        {
            var validAuthority = Uri.TryCreate(options.AuthenticationAuthority, UriKind.Absolute, out var authority)
                && (authority.Scheme == Uri.UriSchemeHttps || authority.Scheme == Uri.UriSchemeHttp)
                && (authority.Scheme == Uri.UriSchemeHttps
                    || !options.IsProduction && !options.AuthenticationRequireHttpsMetadata && authority.IsLoopback);
            AddBoolean(checks, "authentication.oidc", validAuthority && options.AuthenticationAudienceConfigured
                && options.SubjectClaimConfigured && options.TenantClaimConfigured,
                "AUTH_OIDC_VALID", "AUTH_OIDC_CONFIGURATION_INVALID", "OIDC 配置完整且传输策略满足环境要求。",
                "OIDC Authority、metadata HTTPS/loopback 策略、Audience 或必要身份 Claim 配置无效。");
        }
        if (options.IsProduction && options.LegacyApiEnabled)
            AddFailure(checks, "api.legacy", "LEGACY_API_PRODUCTION_ENABLED", "生产环境不得启用旧版 API。");
        else Add(checks, "api.legacy", SystemCheckSeverity.Information, SystemCheckStatus.Passed,
            "LEGACY_API_SAFE", "旧版 API 配置满足环境要求。");
    }

    private void CheckKeysAndWorkflow(List<SystemCheckResult> checks)
    {
        if (!options.MemoryKeyValid || (options.IsProduction && !options.MemoryKeyConfigured))
            AddFailure(checks, "memory.encryption", "MEMORY_KEY_INVALID", "记忆加密密钥未配置或格式无效。");
        else Add(checks, "memory.encryption", SystemCheckSeverity.Critical,
            options.MemoryKeyConfigured ? SystemCheckStatus.Passed : SystemCheckStatus.Warning,
            options.MemoryKeyConfigured ? "MEMORY_KEY_CONFIGURED" : "MEMORY_KEY_DEVELOPMENT_GENERATED",
            options.MemoryKeyConfigured ? "记忆加密密钥已显式配置。" : "当前使用开发环境生成的记忆密钥。");

        if (options.IsProduction && !options.MemoryStorePersistent)
            AddFailure(checks, "memory.persistence", "MEMORY_SQL_REQUIRED",
                "生产环境记忆提案和正式记忆必须使用持久化 SQL Store。");
        else Add(checks, "memory.persistence",
            options.MemoryStorePersistent ? SystemCheckSeverity.Critical : SystemCheckSeverity.Warning,
            options.MemoryStorePersistent ? SystemCheckStatus.Passed : SystemCheckStatus.Warning,
            options.MemoryStorePersistent ? "MEMORY_STORE_PERSISTENT" : "MEMORY_STORE_FILE",
            options.MemoryStorePersistent ? "记忆使用多实例共享的持久化存储。" : "记忆使用单实例文件存储。");

        if (memoryRetentionHealth is { Enabled: true })
            Add(checks, "memory.retention", SystemCheckSeverity.Critical,
                memoryRetentionHealth.IsReady ? SystemCheckStatus.Passed : SystemCheckStatus.Failed,
                memoryRetentionHealth.IsReady ? "MEMORY_RETENTION_HEALTHY" : "MEMORY_RETENTION_FAILED",
                memoryRetentionHealth.IsReady
                    ? "记忆保留期后台清理最近一次执行正常。"
                    : "记忆保留期后台清理失败，readiness 将保持失败直到清理恢复。");

        var sql = Equals(options.WorkflowProvider, "SqlServer");
        var memory = Equals(options.WorkflowProvider, "InMemory");
        if (!sql && !memory) AddFailure(checks, "workflow.provider", "WORKFLOW_PROVIDER_UNSUPPORTED", "工作流存储提供方不受支持。");
        else if (options.IsProduction && !sql) AddFailure(checks, "workflow.provider", "WORKFLOW_SQL_REQUIRED", "生产环境必须使用 SQL Server 持久化工作流。");
        else Add(checks, "workflow.provider", memory ? SystemCheckSeverity.Warning : SystemCheckSeverity.Critical,
            memory ? SystemCheckStatus.Warning : SystemCheckStatus.Passed,
            memory ? "WORKFLOW_IN_MEMORY" : "WORKFLOW_PROVIDER_VALID",
            memory ? "进程内工作流仅适合开发和测试。" : "工作流使用持久化存储。");

        if (sql && (!options.SqlConnectionConfigured || (options.IsProduction
                && (!options.SqlEncrypt || options.SqlTrustServerCertificate))))
            AddFailure(checks, "workflow.sql_tls", "WORKFLOW_SQL_TLS_INVALID", "SQL Server 连接或 TLS 配置不满足环境要求。");
        else if (sql) Add(checks, "workflow.sql_tls", SystemCheckSeverity.Critical, SystemCheckStatus.Passed,
            "WORKFLOW_SQL_TLS_VALID", "SQL Server 连接与 TLS 配置满足环境要求。");

        if (options.IsProduction && !options.AtlasIncidentStorePersistent)
            AddFailure(checks, "workflow.atlas_persistence", "ATLAS_SQL_REQUIRED",
                "生产环境 AtlasID 排查检查点必须使用持久化 SQL Store。");
        else Add(checks, "workflow.atlas_persistence",
            options.AtlasIncidentStorePersistent ? SystemCheckSeverity.Critical : SystemCheckSeverity.Warning,
            options.AtlasIncidentStorePersistent ? SystemCheckStatus.Passed : SystemCheckStatus.Warning,
            options.AtlasIncidentStorePersistent ? "ATLAS_STORE_PERSISTENT" : "ATLAS_STORE_IN_MEMORY",
            options.AtlasIncidentStorePersistent ? "AtlasID 排查检查点使用持久化存储。" : "AtlasID 排查检查点仅保存在当前进程。");

        if (sql && (!options.WorkflowActiveKeyPresent || (options.IsProduction
                && (!options.WorkflowKeyRingConfigured || !options.WorkflowUsesIndependentKey))))
            AddFailure(checks, "workflow.encryption", "WORKFLOW_KEY_RING_INVALID", "工作流密钥环缺失活动密钥或未与记忆密钥隔离。");
        else Add(checks, "workflow.encryption", SystemCheckSeverity.Critical,
            sql && !options.WorkflowKeyRingConfigured ? SystemCheckStatus.Warning : SystemCheckStatus.Passed,
            sql && !options.WorkflowKeyRingConfigured ? "WORKFLOW_KEY_DEVELOPMENT_FALLBACK" : "WORKFLOW_KEY_RING_VALID",
            sql && !options.WorkflowKeyRingConfigured ? "当前使用开发环境工作流密钥回退。" : "工作流密钥环配置有效。");
    }

    private void CheckRag(List<SystemCheckResult> checks)
    {
        var openSearch = Equals(options.RagProvider, "OpenSearch");
        if (!openSearch && !Equals(options.RagProvider, "Local"))
        {
            AddFailure(checks, "rag.provider", "RAG_PROVIDER_UNSUPPORTED", "RAG 提供方不受支持。");
            return;
        }
        if (!openSearch)
        {
            Add(checks, "rag.provider", options.IsProduction ? SystemCheckSeverity.Critical : SystemCheckSeverity.Warning,
                options.IsProduction ? SystemCheckStatus.Failed : SystemCheckStatus.Warning,
                options.IsProduction ? "RAG_PRODUCTION_OPENSEARCH_REQUIRED" : "RAG_LOCAL_PROVIDER",
                options.IsProduction ? "生产环境必须使用受管持久化检索。" : "本地检索仅适合开发和测试。");
            return;
        }
        var https = Uri.TryCreate(options.OpenSearchEndpoint, UriKind.Absolute, out var endpoint)
            && endpoint.Scheme == Uri.UriSchemeHttps;
        if (options.IsProduction && (!https || !options.OpenSearchAuthenticationConfigured))
            AddFailure(checks, "rag.opensearch", "OPENSEARCH_SECURITY_INVALID", "生产 OpenSearch 必须使用 HTTPS 和显式身份认证。");
        else Add(checks, "rag.opensearch", SystemCheckSeverity.Critical,
            https ? SystemCheckStatus.Passed : SystemCheckStatus.Warning,
            https ? "OPENSEARCH_SECURITY_VALID" : "OPENSEARCH_DEVELOPMENT_HTTP",
            https ? "OpenSearch 传输配置满足环境要求。" : "开发 OpenSearch 当前未使用 HTTPS。");
    }

    private void CheckAiProviders(List<SystemCheckResult> checks)
    {
        CheckSandboxProvider(checks, "ai.model", options.ModelProvider, "Deterministic", "MODEL_SANDBOX_PROVIDER");
        CheckSandboxProvider(checks, "ai.embedding", options.EmbeddingProvider, "Deterministic", "EMBEDDING_SANDBOX_PROVIDER");
        CheckSandboxProvider(checks, "ai.reranker", options.RerankerProvider, "Lexical", "RERANKER_SANDBOX_PROVIDER");
        if (options.EmbeddingDimensions <= 0 || options.ExpectedEmbeddingDimensions <= 0
            || options.EmbeddingDimensions != options.ExpectedEmbeddingDimensions)
            AddFailure(checks, "ai.embedding_dimensions", "EMBEDDING_DIMENSIONS_MISMATCH", "嵌入维度与检索索引期望不一致。");
        else Add(checks, "ai.embedding_dimensions", SystemCheckSeverity.Critical, SystemCheckStatus.Passed,
            "EMBEDDING_DIMENSIONS_VALID", "嵌入维度与检索索引期望一致。");
        if (string.IsNullOrWhiteSpace(options.EmbeddingIndexVersion)
            || !string.Equals(options.EmbeddingIndexVersion, options.ExpectedEmbeddingIndexVersion,
                StringComparison.Ordinal))
            AddFailure(checks, "ai.embedding_index", "EMBEDDING_INDEX_VERSION_MISMATCH",
                "嵌入模型绑定的索引版本与当前检索索引不一致，必须重建新索引。");
        else Add(checks, "ai.embedding_index", SystemCheckSeverity.Critical, SystemCheckStatus.Passed,
            "EMBEDDING_INDEX_VERSION_VALID", "嵌入模型与检索索引版本边界一致。");
    }

    private void CheckAgentLease(List<SystemCheckResult> checks)
    {
        var valid = options.AgentMaximumRunTime > TimeSpan.Zero
            && options.AgentResumeLeaseDuration > TimeSpan.Zero
            && options.AgentResumeLeaseRenewalInterval > TimeSpan.Zero
            && options.AgentResumeLeaseRenewalInterval <= options.AgentResumeLeaseDuration / 2;
        AddBoolean(checks, "agent.lease", valid, "AGENT_LEASE_VALID", "AGENT_LEASE_INVALID",
            "Agent 运行预算与恢复租约关系有效。", "Agent 运行预算或恢复租约关系无效。");
    }

    private void CheckSandboxProvider(List<SystemCheckResult> checks, string name, string actual, string sandbox,
        string warningCode)
    {
        var isSandbox = actual.Contains(sandbox, StringComparison.OrdinalIgnoreCase);
        Add(checks, name, isSandbox ? SystemCheckSeverity.Critical : SystemCheckSeverity.Information,
            isSandbox ? (options.IsProduction ? SystemCheckStatus.Failed : SystemCheckStatus.Warning) : SystemCheckStatus.Passed,
            isSandbox ? warningCode : "AI_PROVIDER_PRODUCTION_READY",
            isSandbox ? "当前使用仅供开发与测试的沙箱实现。" : "AI 提供方已显式配置为生产实现。");
    }

    private static bool Equals(string? left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    private static void AddBoolean(List<SystemCheckResult> checks, string name, bool passed, string passCode,
        string failureCode, string passMessage, string failureMessage) =>
        Add(checks, name, SystemCheckSeverity.Critical, passed ? SystemCheckStatus.Passed : SystemCheckStatus.Failed,
            passed ? passCode : failureCode, passed ? passMessage : failureMessage);
    private static void AddFailure(List<SystemCheckResult> checks, string name, string code, string message) =>
        Add(checks, name, SystemCheckSeverity.Critical, SystemCheckStatus.Failed, code, message);
    private static void Add(List<SystemCheckResult> checks, string name, SystemCheckSeverity severity,
        SystemCheckStatus status, string code, string message) => checks.Add(new(name, severity, status, code, message));
}
