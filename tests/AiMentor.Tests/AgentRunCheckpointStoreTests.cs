using System.Text.Json;
using AiMentor.Application;
using AiMentor.Domain;
using AiMentor.Infrastructure;
using Xunit;

namespace AiMentor.Tests;

public sealed class AgentRunCheckpointStoreTests
{
    [Fact]
    public async Task LeaseShouldEnforceOwnerExclusionTokenAndExpiryTakeover()
    {
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 7, 13, 12, 0, 0, TimeSpan.Zero));
        var store = new InMemoryAgentRunCheckpointStore(clock);
        var owner = AccessContext.Create("tenant-a", "user-a", ["readers"]);
        await store.SavePendingAsync(CreateCheckpoint(owner, clock.GetUtcNow()));

        var forbidden = await store.TryAcquireAsync("run-1",
            AccessContext.Create("tenant-a", "user-b", ["readers"]), "node-b", TimeSpan.FromSeconds(30));
        var first = await store.TryAcquireAsync("run-1", owner, "node-a", TimeSpan.FromSeconds(30));
        var busy = await store.TryAcquireAsync("run-1", owner, "node-b", TimeSpan.FromSeconds(30));
        await store.ReleaseAsync("run-1", "wrong-token");
        var stillBusy = await store.TryAcquireAsync("run-1", owner, "node-b", TimeSpan.FromSeconds(30));
        clock.Advance(TimeSpan.FromSeconds(20));
        var wrongToken = await store.RenewAsync("run-1", "wrong-token", "node-a", TimeSpan.FromSeconds(30));
        var wrongOwner = await store.RenewAsync("run-1", first.LeaseToken!, "node-b", TimeSpan.FromSeconds(30));
        var renewed = await store.RenewAsync("run-1", first.LeaseToken!, "node-a", TimeSpan.FromSeconds(30));
        clock.Advance(TimeSpan.FromSeconds(11));
        var protectedByRenewal = await store.TryAcquireAsync("run-1", owner, "node-b", TimeSpan.FromSeconds(30));
        clock.Advance(TimeSpan.FromSeconds(20));
        var takeover = await store.TryAcquireAsync("run-1", owner, "node-b", TimeSpan.FromSeconds(30));

        Assert.Equal(AgentRunLeaseStatus.Forbidden, forbidden.Status);
        Assert.Equal(AgentRunLeaseStatus.Acquired, first.Status);
        Assert.Equal(AgentRunLeaseStatus.Busy, busy.Status);
        Assert.Equal(AgentRunLeaseStatus.Busy, stillBusy.Status);
        Assert.False(wrongToken);
        Assert.False(wrongOwner);
        Assert.True(renewed);
        Assert.Equal(AgentRunLeaseStatus.Busy, protectedByRenewal.Status);
        Assert.Equal(AgentRunLeaseStatus.Acquired, takeover.Status);
        Assert.NotEqual(first.LeaseToken, takeover.LeaseToken);
    }

    [Fact]
    public async Task SaveAfterResumeShouldRequireCurrentLeaseToken()
    {
        var clock = new MutableTimeProvider(DateTimeOffset.UtcNow);
        var store = new InMemoryAgentRunCheckpointStore(clock);
        var owner = AccessContext.Create("tenant-a", "user-a", ["readers"]);
        var checkpoint = CreateCheckpoint(owner, clock.GetUtcNow());
        await store.SavePendingAsync(checkpoint);
        var lease = await store.TryAcquireAsync(checkpoint.RunId, owner, "node-a", TimeSpan.FromSeconds(30));

        var conflict = await Assert.ThrowsAsync<AgentRunWorkflowException>(() =>
            store.SavePendingAsync(checkpoint with { ApprovalId = "approval-2" }, "wrong-token"));
        await store.SavePendingAsync(checkpoint with { ApprovalId = "approval-2" }, lease.LeaseToken);
        var reacquired = await store.TryAcquireAsync(checkpoint.RunId, owner, "node-b", TimeSpan.FromSeconds(30));

        Assert.Equal("AGENT_CHECKPOINT_CONFLICT", conflict.Code);
        Assert.Equal(AgentRunLeaseStatus.Acquired, reacquired.Status);
        Assert.Equal("approval-2", reacquired.Checkpoint!.ApprovalId);
    }

    private static AgentRunCheckpoint CreateCheckpoint(AccessContext access, DateTimeOffset now) => new(
        "run-1", access, "approval-1", "framework-1", "call-1", "memory_delete",
        new Dictionary<string, object?>(), JsonSerializer.SerializeToElement(new { }), [], [], [], 0,
        new AgentApprovalCheckpoint("approval-1", "memory.delete", ToolOperationRisk.Mutation, ["memoryId"],
            now, now.AddMinutes(15)), now, now.AddMinutes(15));

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now = _now.Add(duration);
    }
}
