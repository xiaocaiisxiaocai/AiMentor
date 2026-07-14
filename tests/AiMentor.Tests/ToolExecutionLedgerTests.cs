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
        var reserved = await ledger.TryAcquireAsync(Key('A'), Fingerprint('A'), "run-1",
            TimeSpan.FromSeconds(30), TimeSpan.FromHours(1), 100);
        clock.Advance(TimeSpan.FromSeconds(31));
        var safeTakeover = await ledger.TryAcquireAsync(Key('A'), Fingerprint('A'), "run-2",
            TimeSpan.FromSeconds(30), TimeSpan.FromHours(1), 100);

        var executing = await ledger.TryAcquireAsync(Key('B'), Fingerprint('B'), "run-3",
            TimeSpan.FromSeconds(30), TimeSpan.FromHours(1), 100);
        await ledger.MarkExecutingAsync(Key('B'), executing.LeaseToken!);
        clock.Advance(TimeSpan.FromSeconds(31));
        var uncertain = await ledger.TryAcquireAsync(Key('B'), Fingerprint('B'), "run-4",
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
        var acquired = await ledger.TryAcquireAsync(Key('C'), Fingerprint('C'), "run-1",
            TimeSpan.FromSeconds(30), TimeSpan.FromHours(1), 100);
        await ledger.MarkExecutingAsync(Key('C'), acquired.LeaseToken!);
        var result = Result("run-1");
        await ledger.CompleteAsync(Key('C'), acquired.LeaseToken!, result);

        var replay = await ledger.TryAcquireAsync(Key('C'), Fingerprint('C'), "run-2",
            TimeSpan.FromSeconds(30), TimeSpan.FromHours(1), 100);
        var mismatch = await ledger.TryAcquireAsync(Key('C'), Fingerprint('D'), "run-3",
            TimeSpan.FromSeconds(30), TimeSpan.FromHours(1), 100);

        Assert.Equal(IdempotencyAcquireStatus.Replay, replay.Status);
        Assert.Equal(result.RunId, replay.ReplayResult!.RunId);
        Assert.Equal(IdempotencyAcquireStatus.FingerprintMismatch, mismatch.Status);
    }

    private static string Key(char value) => new(value, 64);
    private static string Fingerprint(char value) => new(value, 64);
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
