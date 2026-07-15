using System.Text.Json;
using AiMentor.Application;
using AiMentor.Domain;
using AiMentor.Infrastructure;
using Xunit;

namespace AiMentor.Tests;

public sealed class AtlasIncidentWorkflowTests
{
    private static readonly AccessContext Owner = new("tenant-a", "user-a", new HashSet<string>());
    private static readonly DateTimeOffset Now = new(2026, 7, 15, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task RawTokenIsRejectedBeforeCheckpointIsCreated()
    {
        var fixture = Create();
        var exception = await Assert.ThrowsAsync<AtlasIncidentWorkflowException>(() =>
            fixture.Service.StartAsync(new AtlasIncidentInput(RawToken: "eyJhbGciOiJSUzI1NiJ9.secret.signature"), Owner));

        Assert.Equal("ATLAS_RAW_TOKEN_FORBIDDEN", exception.Code);
    }

    [Fact]
    public async Task MissingInputsPauseAndCanBeCompletedIncrementally()
    {
        var fixture = Create();
        var initial = await fixture.Service.StartAsync(new AtlasIncidentInput(Region: "cn-north"), Owner);

        Assert.Equal(AtlasIncidentStatus.RequiredInputs, initial.Status);
        Assert.Contains("node", initial.RequiredInputs);

        var completed = await fixture.Service.ResumeAsync(initial.RunId, initial.Version, CompleteInput(), Owner);
        Assert.Equal(AtlasIncidentStatus.DiagnosisReady, completed.Status);
        Assert.Empty(completed.RequiredInputs);
        Assert.DoesNotContain("RawToken", JsonSerializer.Serialize(completed), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CrossUserReadAndResumeAreForbidden()
    {
        var fixture = Create();
        var run = await fixture.Service.StartAsync(CompleteInput(), Owner);
        var other = Owner with { SubjectId = "user-b" };

        var read = await Assert.ThrowsAsync<AtlasIncidentWorkflowException>(() => fixture.Service.GetAsync(run.RunId, other));
        var resume = await Assert.ThrowsAsync<AtlasIncidentWorkflowException>(() =>
            fixture.Service.ResumeAsync(run.RunId, run.Version, new AtlasIncidentInput(NodeUtcOffsetSeconds: 1), other));
        Assert.Equal("ATLAS_RUN_FORBIDDEN", read.Code);
        Assert.Equal("ATLAS_RUN_FORBIDDEN", resume.Code);
    }

    [Fact]
    public async Task ConcurrentResumeHasExactlyOneVersionWinner()
    {
        var fixture = Create();
        var run = await fixture.Service.StartAsync(new AtlasIncidentInput(Region: "cn-north"), Owner);

        async Task<string> ResumeAsync()
        {
            try
            {
                await fixture.Service.ResumeAsync(run.RunId, run.Version, CompleteInput(), Owner);
                return "won";
            }
            catch (AtlasIncidentWorkflowException exception) { return exception.Code; }
        }
        var results = await Task.WhenAll(ResumeAsync(), ResumeAsync());

        Assert.Single(results, result => result == "won");
        Assert.Single(results, result => result is "ATLAS_VERSION_CONFLICT" or "ATLAS_RUN_BUSY");
    }

    [Fact]
    public async Task CancelledAndExpiredRunsCannotAdvance()
    {
        var fixture = Create(retention: TimeSpan.FromMinutes(5));
        var cancelledSource = await fixture.Service.StartAsync(new AtlasIncidentInput(Region: "cn"), Owner);
        var cancelled = await fixture.Service.CancelAsync(cancelledSource.RunId, cancelledSource.Version, Owner);
        Assert.Equal(AtlasIncidentStatus.Cancelled, cancelled.Status);
        var cancelError = await Assert.ThrowsAsync<AtlasIncidentWorkflowException>(() => fixture.Service.ResumeAsync(
            cancelled.RunId, cancelled.Version, CompleteInput(), Owner));
        Assert.Equal("ATLAS_RUN_TERMINAL", cancelError.Code);

        var expiring = await fixture.Service.StartAsync(new AtlasIncidentInput(Region: "cn"), Owner);
        fixture.Clock.Advance(TimeSpan.FromMinutes(6));
        var expired = await fixture.Service.GetAsync(expiring.RunId, Owner);
        Assert.Equal(AtlasIncidentStatus.Expired, expired.Status);
        var expiryError = await Assert.ThrowsAsync<AtlasIncidentWorkflowException>(() => fixture.Service.ResumeAsync(
            expired.RunId, expired.Version, CompleteInput(), Owner));
        Assert.Equal("ATLAS_RUN_TERMINAL", expiryError.Code);
    }

    [Fact]
    public async Task ClockSkewMakesNtpRecoveryTheFirstFinding()
    {
        var fixture = Create();
        var run = await fixture.Service.StartAsync(CompleteInput() with
        {
            NodeUtcOffsetSeconds = 180,
            TokenMetadata = CompleteInput().TokenMetadata! with { NotBefore = Now.AddMinutes(1) }
        }, Owner);

        Assert.Equal("CLOCK_SKEW", run.Findings[0].Code);
        Assert.Contains("NTP", run.Findings[0].Recommendation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SignatureFailureNeverRecommendsRotatingAllKeys()
    {
        var fixture = Create();
        var run = await fixture.Service.StartAsync(CompleteInput() with
        {
            TokenMetadata = CompleteInput().TokenMetadata! with { SignatureValid = false }
        }, Owner);

        var finding = Assert.Single(run.Findings, item => item.Code == "SIGNATURE_VALIDATION_FAILED");
        Assert.Contains("不能", finding.Recommendation);
        Assert.Contains("全部密钥", finding.Recommendation);
    }

    [Fact]
    public async Task JwksAndRecentChangesProduceSeparateStructuredFindings()
    {
        var fixture = Create();
        var run = await fixture.Service.StartAsync(CompleteInput() with
        {
            JwksCacheStale = true,
            RecentIdentityConfigurationChange = true
        }, Owner);

        Assert.Contains(run.Findings, item => item.Code == "JWKS_CACHE_STALE");
        Assert.Contains(run.Findings, item => item.Code == "RECENT_IDENTITY_CHANGE");
    }

    [Fact]
    public async Task ProposedMutationOnlyCreatesAwaitingApprovalState()
    {
        var fixture = Create();
        var run = await fixture.Service.StartAsync(CompleteInput() with { ProposedAction = "刷新节点 JWKS 缓存" }, Owner);

        Assert.Equal(AtlasIncidentStatus.AwaitingActionApproval, run.Status);
        Assert.Equal("刷新节点 JWKS 缓存", run.ProposedAction);
    }

    [Fact]
    public async Task ReconstructedServiceCanResumeUsingSharedStoreButStoreIsProcessLocal()
    {
        var clock = new ManualTimeProvider(Now);
        var store = new InMemoryAtlasIncidentStore(clock);
        var options = new AtlasIncidentWorkflowOptions();
        var first = new AtlasIncidentWorkflow(store, options, clock);
        var run = await first.StartAsync(new AtlasIncidentInput(Region: "cn"), Owner);

        var reconstructed = new AtlasIncidentWorkflow(store, options, clock);
        var resumed = await reconstructed.ResumeAsync(run.RunId, run.Version, CompleteInput(), Owner);

        Assert.Equal(AtlasIncidentStatus.DiagnosisReady, resumed.Status);
    }

    [Fact]
    public async Task RunbookVersionMismatchFailsClosed()
    {
        var clock = new ManualTimeProvider(Now);
        var store = new InMemoryAtlasIncidentStore(clock);
        var original = new AtlasIncidentWorkflow(store, new AtlasIncidentWorkflowOptions(), clock);
        var run = await original.StartAsync(new AtlasIncidentInput(Region: "cn"), Owner);
        var changed = new AtlasIncidentWorkflow(store,
            new AtlasIncidentWorkflowOptions { RunbookVersion = "2.3" }, clock);

        var exception = await Assert.ThrowsAsync<AtlasIncidentWorkflowException>(() =>
            changed.ResumeAsync(run.RunId, run.Version, CompleteInput(), Owner));
        Assert.Equal("ATLAS_RUNBOOK_VERSION_MISMATCH", exception.Code);
    }

    private static AtlasIncidentInput CompleteInput() => new(
        Region: "cn-north", Node: "atlas-01", ObservedAt: Now,
        TokenMetadata: new AtlasTokenMetadata(Now.AddHours(1), Now.AddMinutes(-1), true, true, true),
        NodeUtcOffsetSeconds: 0, JwksCacheStale: false, RecentIdentityConfigurationChange: false);

    private static Fixture Create(TimeSpan? retention = null)
    {
        var clock = new ManualTimeProvider(Now);
        var store = new InMemoryAtlasIncidentStore(clock);
        var options = new AtlasIncidentWorkflowOptions { Retention = retention ?? TimeSpan.FromHours(24) };
        return new Fixture(new AtlasIncidentWorkflow(store, options, clock), clock);
    }

    private sealed record Fixture(AtlasIncidentWorkflow Service, ManualTimeProvider Clock);

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;
        public override DateTimeOffset GetUtcNow() => _utcNow;
        public void Advance(TimeSpan value) => _utcNow += value;
    }
}
