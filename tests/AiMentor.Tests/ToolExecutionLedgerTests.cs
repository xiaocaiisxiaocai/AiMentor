using System.Text.Json;
using AiMentor.Application;
using AiMentor.Domain;
using AiMentor.Infrastructure;
using Xunit;

namespace AiMentor.Tests;

public sealed class ToolExecutionLedgerTests
{
    [Fact]
    public async Task ExpiredReservationCanRetryButExpiredExecutionMustBecomeOutcomeUnknown()
    {
        var clock = new MutableTimeProvider(DateTimeOffset.UtcNow);
        var ledger = new InMemoryToolExecutionLedger(clock);
        var reserved = await ledger.TryAcquireAsync(Request('A', 'A', "run-1", "tenant-a"),
            TimeSpan.FromSeconds(30), TimeSpan.FromHours(1), 100);
        clock.Advance(TimeSpan.FromSeconds(31));
        var safeTakeover = await ledger.TryAcquireAsync(Request('A', 'A', "run-2", "tenant-a"),
            TimeSpan.FromSeconds(30), TimeSpan.FromHours(1), 100);

        var executing = await ledger.TryAcquireAsync(Request('B', 'B', "run-3", "tenant-a"),
            TimeSpan.FromSeconds(30), TimeSpan.FromHours(1), 100);
        await ledger.MarkExecutingAsync(Key('B'), executing.LeaseToken!);
        clock.Advance(TimeSpan.FromSeconds(31));
        var uncertain = await ledger.TryAcquireAsync(Request('B', 'B', "run-4", "tenant-a"),
            TimeSpan.FromSeconds(30), TimeSpan.FromHours(1), 100);

        Assert.Equal(IdempotencyAcquireStatus.Acquired, reserved.Status);
        Assert.Equal(IdempotencyAcquireStatus.Acquired, safeTakeover.Status);
        Assert.NotEqual(reserved.LeaseToken, safeTakeover.LeaseToken);
        Assert.Equal(IdempotencyAcquireStatus.OutcomeUnknown, uncertain.Status);
    }

    [Fact]
    public async Task CompletedResultShouldReplayAndFingerprintMismatchShouldRefuse()
    {
        var ledger = new InMemoryToolExecutionLedger(TimeProvider.System);
        var acquired = await ledger.TryAcquireAsync(Request('C', 'C', "run-1", "tenant-a"),
            TimeSpan.FromSeconds(30), TimeSpan.FromHours(1), 100);
        await ledger.MarkExecutingAsync(Key('C'), acquired.LeaseToken!);
        var result = Result("run-1");
        await ledger.CompleteAsync(Key('C'), acquired.LeaseToken!, result);

        var replay = await ledger.TryAcquireAsync(Request('C', 'C', "run-2", "tenant-a"),
            TimeSpan.FromSeconds(30), TimeSpan.FromHours(1), 100);
        var mismatch = await ledger.TryAcquireAsync(Request('C', 'D', "run-3", "tenant-a"),
            TimeSpan.FromSeconds(30), TimeSpan.FromHours(1), 100);

        Assert.Equal(IdempotencyAcquireStatus.Replay, replay.Status);
        Assert.Equal(result.RunId, replay.ReplayResult!.RunId);
        Assert.Equal(IdempotencyAcquireStatus.FingerprintMismatch, mismatch.Status);
    }

    [Fact]
    public async Task OutcomeUnknownQueryShouldBeTenantIsolatedAndMinimal()
    {
        var ledger = new InMemoryToolExecutionLedger(TimeProvider.System);
        foreach (var (key, tenant) in new[] { ('D', "tenant-a"), ('E', "tenant-b") })
        {
            var acquired = await ledger.TryAcquireAsync(Request(key, key, $"run-{key}", tenant),
                TimeSpan.FromSeconds(30), TimeSpan.FromHours(1), 100);
            await ledger.MarkExecutingAsync(Key(key), acquired.LeaseToken!);
            await ledger.MarkOutcomeUnknownAsync(Key(key), acquired.LeaseToken!);
        }

        var records = await ledger.ListOutcomeUnknownAsync("tenant-a", 50);

        var record = Assert.Single(records);
        Assert.Equal("tenant-a", record.TenantId);
        Assert.Equal("subject-a", record.SubjectId);
        Assert.Equal("memory.delete", record.ToolName);
        Assert.Equal(Key('D'), record.ExecutionKey);
    }

    [Fact]
    public async Task TwoDifferentReviewersShouldResolveAppliedOutcome()
    {
        var clock = new MutableTimeProvider(DateTimeOffset.UtcNow);
        var ledger = new InMemoryToolExecutionLedger(clock);
        var acquired = await ledger.TryAcquireAsync(Request('F', 'F', "run-f", "tenant-a"),
            TimeSpan.FromSeconds(30), TimeSpan.FromHours(1), 100);
        await ledger.MarkExecutingAsync(Key('F'), acquired.LeaseToken!);
        await ledger.MarkOutcomeUnknownAsync(Key('F'), acquired.LeaseToken!);
        var first = await ledger.SubmitReconciliationReviewAsync(Review('F', "reviewer-1",
            ToolOutcomeProbeState.Applied, clock.GetUtcNow()));
        var sameReviewer = await ledger.SubmitReconciliationReviewAsync(Review('F', "reviewer-1",
            ToolOutcomeProbeState.Applied, clock.GetUtcNow()));
        var second = await ledger.SubmitReconciliationReviewAsync(Review('F', "reviewer-2",
            ToolOutcomeProbeState.Applied, clock.GetUtcNow()));
        var retry = await ledger.TryAcquireAsync(Request('F', 'F', "run-retry", "tenant-a"),
            TimeSpan.FromSeconds(30), TimeSpan.FromHours(1), 100);

        Assert.Equal(ToolReconciliationReviewStatus.AwaitingSecondReviewer, first.Status);
        Assert.Equal(ToolReconciliationReviewStatus.ReviewerMustDiffer, sameReviewer.Status);
        Assert.Equal(ToolReconciliationReviewStatus.ResolvedApplied, second.Status);
        Assert.Equal(IdempotencyAcquireStatus.ReconciledApplied, retry.Status);
    }

    [Fact]
    public async Task NotAppliedResolutionShouldPreserveSingleRetryAuthorizationWhenAbandoned()
    {
        var clock = new MutableTimeProvider(DateTimeOffset.UtcNow);
        var ledger = new InMemoryToolExecutionLedger(clock);
        var acquired = await ledger.TryAcquireAsync(Request('G', 'G', "run-g", "tenant-a"),
            TimeSpan.FromSeconds(30), TimeSpan.FromHours(1), 100);
        await ledger.MarkExecutingAsync(Key('G'), acquired.LeaseToken!);
        await ledger.MarkOutcomeUnknownAsync(Key('G'), acquired.LeaseToken!);
        await ledger.SubmitReconciliationReviewAsync(Review('G', "reviewer-1",
            ToolOutcomeProbeState.NotApplied, clock.GetUtcNow()));
        var resolved = await ledger.SubmitReconciliationReviewAsync(Review('G', "reviewer-2",
            ToolOutcomeProbeState.NotApplied, clock.GetUtcNow()));
        var firstRetry = await ledger.TryAcquireAsync(Request('G', 'G', "retry-1", "tenant-a"),
            TimeSpan.FromSeconds(30), TimeSpan.FromHours(1), 100);
        await ledger.AbandonAsync(Key('G'), firstRetry.LeaseToken!);
        var secondRetry = await ledger.TryAcquireAsync(Request('G', 'G', "retry-2", "tenant-a"),
            TimeSpan.FromSeconds(30), TimeSpan.FromHours(1), 100);

        Assert.Equal(ToolReconciliationReviewStatus.RetryAuthorized, resolved.Status);
        Assert.Equal(IdempotencyAcquireStatus.Acquired, firstRetry.Status);
        Assert.Equal(IdempotencyAcquireStatus.Acquired, secondRetry.Status);
    }

    private static string Key(char value) => new(value, 64);
    private static string Fingerprint(char value) => new(value, 64);
    private static ToolExecutionLedgerRequest Request(char key, char fingerprint, string runId, string tenantId) =>
        new(Key(key), Fingerprint(fingerprint), runId, tenantId, "subject-a", "memory.delete");
    private static ToolReconciliationReview Review(char key, string reviewer,
        ToolOutcomeProbeState state, DateTimeOffset now) => new(Key(key), "tenant-a", reviewer, state,
            state == ToolOutcomeProbeState.Applied ? "APPLIED" : "NOT_APPLIED", now, now.AddMinutes(5), true,
            new string('A', 64));
    private static ToolExecutionResult Result(string runId) => new(runId, "memory.delete",
        ToolExecutionStatus.Completed, JsonSerializer.SerializeToElement(new { deleted = true }),
        SafetyDecision.Allowed, false, []);

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now = _now.Add(duration);
    }
}
