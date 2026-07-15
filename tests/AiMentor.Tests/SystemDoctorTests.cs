using System.Text.Json;
using AiMentor.Application;
using AiMentor.Infrastructure;
using Xunit;

namespace AiMentor.Tests;

public sealed class SystemDoctorTests
{
    [Fact]
    public async Task ProductionConfigurationWithSecureProvidersIsReady()
    {
        var report = await RunAsync(ProductionOptions());

        Assert.True(report.IsReady);
        Assert.DoesNotContain(report.Checks, item => item.Status == SystemCheckStatus.Failed);
    }

    [Fact]
    public async Task ProductionDevelopmentIdentityFailsClosed()
    {
        var report = await RunAsync(ProductionOptions() with { AuthenticationMode = "Development" });

        Assert.False(report.IsReady);
        Assert.Contains(report.Checks, item => item.Code == "AUTH_PRODUCTION_OIDC_REQUIRED"
            && item.Severity == SystemCheckSeverity.Critical);
    }

    [Fact]
    public async Task ProductionRejectsInsecureOpenSearchAndMissingAuthentication()
    {
        var report = await RunAsync(ProductionOptions() with
        {
            OpenSearchEndpoint = "http://search.internal:9200",
            OpenSearchAuthenticationConfigured = false
        });

        Assert.False(report.IsReady);
        Assert.Contains(report.Checks, item => item.Code == "OPENSEARCH_SECURITY_INVALID");
    }

    [Fact]
    public async Task ProductionRejectsSharedWorkflowKeyAndUnsafeSqlTls()
    {
        var report = await RunAsync(ProductionOptions() with
        {
            WorkflowUsesIndependentKey = false,
            SqlTrustServerCertificate = true
        });

        Assert.False(report.IsReady);
        Assert.Contains(report.Checks, item => item.Code == "WORKFLOW_KEY_RING_INVALID");
        Assert.Contains(report.Checks, item => item.Code == "WORKFLOW_SQL_TLS_INVALID");
    }

    [Fact]
    public async Task ProductionRejectsProcessLocalAtlasIncidentStore()
    {
        var report = await RunAsync(ProductionOptions() with { AtlasIncidentStorePersistent = false });

        Assert.False(report.IsReady);
        Assert.Contains(report.Checks, item => item.Code == "ATLAS_SQL_REQUIRED");
    }

    [Fact]
    public async Task ProductionRejectsProcessLocalMemoryStore()
    {
        var report = await RunAsync(ProductionOptions() with { MemoryStorePersistent = false });

        Assert.False(report.IsReady);
        Assert.Contains(report.Checks, item => item.Code == "MEMORY_SQL_REQUIRED");
    }

    [Fact]
    public async Task RetentionFailureKeepsReadinessRedUntilNextSuccessfulPurge()
    {
        var health = new MemoryRetentionHealthState(true);
        health.MarkFailure();
        var doctor = new SystemDoctor(ProductionOptions(), TimeProvider.System, health);

        var failed = await doctor.RunAsync();
        Assert.False(failed.IsReady);
        Assert.Contains(failed.Checks, item => item.Code == "MEMORY_RETENTION_FAILED");

        health.MarkSuccess();
        var recovered = await doctor.RunAsync();
        Assert.True(recovered.IsReady);
        Assert.Contains(recovered.Checks, item => item.Code == "MEMORY_RETENTION_HEALTHY");
    }

    [Fact]
    public async Task ProductionRejectsSandboxProvidersAndEmbeddingDimensionMismatch()
    {
        var report = await RunAsync(ProductionOptions() with
        {
            ModelProvider = nameof(DeterministicGroundedChatClient),
            EmbeddingProvider = nameof(DeterministicEmbeddingGenerator),
            RerankerProvider = nameof(LexicalEvidenceReranker),
            EmbeddingDimensions = 256,
            ExpectedEmbeddingDimensions = 1536
        });

        Assert.False(report.IsReady);
        Assert.Contains(report.Checks, item => item.Code == "MODEL_SANDBOX_PROVIDER");
        Assert.Contains(report.Checks, item => item.Code == "EMBEDDING_SANDBOX_PROVIDER");
        Assert.Contains(report.Checks, item => item.Code == "RERANKER_SANDBOX_PROVIDER");
        Assert.Contains(report.Checks, item => item.Code == "EMBEDDING_DIMENSIONS_MISMATCH");
    }

    [Fact]
    public async Task InvalidLeaseRelationshipFailsAndReportNeverContainsSecrets()
    {
        const string secret = "Server=db;Password=top-secret-value";
        var report = await RunAsync(ProductionOptions() with
        {
            AgentResumeLeaseDuration = TimeSpan.FromSeconds(10),
            AgentResumeLeaseRenewalInterval = TimeSpan.FromSeconds(6),
            AuthenticationAuthority = $"https://identity.example/{secret}"
        });

        Assert.False(report.IsReady);
        Assert.Contains(report.Checks, item => item.Code == "AGENT_LEASE_INVALID");
        Assert.DoesNotContain(secret, JsonSerializer.Serialize(report), StringComparison.Ordinal);
        Assert.DoesNotContain("Password=", JsonSerializer.Serialize(report), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DevelopmentDefaultsRemainRunnableWithExplicitWarnings()
    {
        var options = new SystemDoctorOptions
        {
            AuthenticationMode = "Development",
            WorkflowProvider = "InMemory",
            RagProvider = "Local",
            ModelProvider = nameof(DeterministicGroundedChatClient),
            EmbeddingProvider = nameof(DeterministicEmbeddingGenerator),
            RerankerProvider = nameof(LexicalEvidenceReranker),
            EmbeddingDimensions = 256,
            ExpectedEmbeddingDimensions = 256,
            EmbeddingIndexVersion = "aimentor-knowledge-v1",
            ExpectedEmbeddingIndexVersion = "aimentor-knowledge-v1",
            WorkflowActiveKeyPresent = true,
            AgentMaximumRunTime = TimeSpan.FromSeconds(10),
            AgentResumeLeaseDuration = TimeSpan.FromSeconds(30),
            AgentResumeLeaseRenewalInterval = TimeSpan.FromSeconds(10)
        };

        var report = await RunAsync(options);

        Assert.True(report.IsReady);
        Assert.Contains(report.Checks, item => item.Status == SystemCheckStatus.Warning);
    }

    [Fact]
    public async Task LoopbackHttpOidcIsAcceptedOnlyOutsideProductionWithExplicitOptIn()
    {
        var development = ProductionOptions() with
        {
            IsProduction = false,
            AuthenticationAuthority = "http://127.0.0.1:5100",
            AuthenticationRequireHttpsMetadata = false
        };
        Assert.True((await RunAsync(development)).IsReady);

        var production = development with { IsProduction = true };
        var report = await RunAsync(production);
        Assert.False(report.IsReady);
        Assert.Contains(report.Checks, item => item.Code == "AUTH_OIDC_CONFIGURATION_INVALID");

        var remoteHttp = development with { AuthenticationAuthority = "http://identity.internal:5100" };
        Assert.False((await RunAsync(remoteHttp)).IsReady);
    }

    private static Task<SystemDiagnosticReport> RunAsync(SystemDoctorOptions options) =>
        new SystemDoctor(options, TimeProvider.System).RunAsync();

    private static SystemDoctorOptions ProductionOptions() => new()
    {
        IsProduction = true,
        AuthenticationMode = "OidcJwt",
        AuthenticationAuthority = "https://identity.example",
        AuthenticationRequireHttpsMetadata = true,
        AuthenticationAudienceConfigured = true,
        SubjectClaimConfigured = true,
        TenantClaimConfigured = true,
        WorkflowProvider = "SqlServer",
        AtlasIncidentStorePersistent = true,
        MemoryStorePersistent = true,
        MemoryKeyConfigured = true,
        MemoryKeyValid = true,
        WorkflowKeyRingConfigured = true,
        WorkflowActiveKeyPresent = true,
        WorkflowUsesIndependentKey = true,
        SqlConnectionConfigured = true,
        SqlEncrypt = true,
        SqlTrustServerCertificate = false,
        RagProvider = "OpenSearch",
        OpenSearchEndpoint = "https://search.internal:9200",
        OpenSearchAuthenticationConfigured = true,
        ModelProvider = "AzureOpenAI",
        EmbeddingProvider = "AzureOpenAI",
        RerankerProvider = "Semantic",
        EmbeddingDimensions = 1536,
        ExpectedEmbeddingDimensions = 1536,
        EmbeddingIndexVersion = "aimentor-knowledge-v2",
        ExpectedEmbeddingIndexVersion = "aimentor-knowledge-v2",
        AgentMaximumRunTime = TimeSpan.FromSeconds(10),
        AgentResumeLeaseDuration = TimeSpan.FromSeconds(30),
        AgentResumeLeaseRenewalInterval = TimeSpan.FromSeconds(10)
    };

}
