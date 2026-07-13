using AiMentor.Application;
using AiMentor.Domain;
using AiMentor.Infrastructure;
using Xunit;

namespace AiMentor.Tests;

public sealed class MemoryWorkflowTests
{
    private static readonly AccessContext Owner = AccessContext.Create("tenant-a", "user-a", ["all-rnd"]);

    [Fact]
    public async Task ProposalShouldRemainInvisibleUntilExplicitApproval()
    {
        var (service, _) = CreateService();
        var proposal = await service.ProposeAsync(
            new ProposeMemoryCommand(MemoryScope.UserPreference, "answer.length", "简短"), Owner);

        Assert.Equal(MemoryProposalStatus.PendingApproval, proposal.Status);
        Assert.Empty(await service.ListAsync(Owner));

        var memory = await service.ApproveAsync(proposal.Id, Owner);
        var visible = await service.ListAsync(Owner);

        Assert.Equal(1, memory.Version);
        Assert.Single(visible);
        Assert.Equal(memory.Id, visible[0].Id);
    }

    [Fact]
    public async Task ProposalAndMemoryShouldBeIsolatedByTenantAndSubject()
    {
        var (service, _) = CreateService();
        var proposal = await service.ProposeAsync(
            new ProposeMemoryCommand(MemoryScope.LongTermFact, "project", "OrionOrder"), Owner);
        var otherTenant = AccessContext.Create("tenant-b", "user-a", ["all-rnd"]);
        var otherUser = AccessContext.Create("tenant-a", "user-b", ["all-rnd"]);

        var exception = await Assert.ThrowsAsync<MemoryWorkflowException>(() => service.ApproveAsync(proposal.Id, otherTenant));
        Assert.Equal("MEMORY_PROPOSAL_NOT_FOUND", exception.Code);

        await service.ApproveAsync(proposal.Id, Owner);
        Assert.Empty(await service.ListAsync(otherTenant));
        Assert.Empty(await service.ListAsync(otherUser));
        Assert.Single(await service.ListAsync(Owner));
    }

    [Fact]
    public async Task StaleVersionShouldNotOverwriteOrDeleteNewerMemory()
    {
        var (service, _) = CreateService();
        var proposal = await service.ProposeAsync(
            new ProposeMemoryCommand(MemoryScope.UserPreference, "answer.language", "中文"), Owner);
        var memory = await service.ApproveAsync(proposal.Id, Owner);
        var updated = await service.CorrectAsync(memory.Id, new CorrectMemoryCommand("中英双语", memory.Version), Owner);

        var updateConflict = await Assert.ThrowsAsync<MemoryWorkflowException>(() =>
            service.CorrectAsync(memory.Id, new CorrectMemoryCommand("英文", memory.Version), Owner));
        var deleteConflict = await Assert.ThrowsAsync<MemoryWorkflowException>(() =>
            service.DeleteAsync(memory.Id, memory.Version, Owner));

        Assert.Equal(2, updated.Version);
        Assert.Equal("MEMORY_VERSION_CONFLICT", updateConflict.Code);
        Assert.Equal("MEMORY_VERSION_CONFLICT", deleteConflict.Code);
        Assert.Equal("中英双语", Assert.Single(await service.ListAsync(Owner)).Value);
    }

    [Fact]
    public async Task ExpiredProposalAndMemoryShouldNotBecomeUsable()
    {
        var (service, clock) = CreateService();
        var expiredProposal = await service.ProposeAsync(
            new ProposeMemoryCommand(MemoryScope.UserPreference, "tone", "正式"), Owner);
        clock.Advance(TimeSpan.FromMinutes(16));

        var approvalException = await Assert.ThrowsAsync<MemoryWorkflowException>(() =>
            service.ApproveAsync(expiredProposal.Id, Owner));
        Assert.Equal("MEMORY_PROPOSAL_EXPIRED", approvalException.Code);

        var shortLived = await service.ProposeAsync(new ProposeMemoryCommand(MemoryScope.Session, "task", "排查故障",
            "session-1", clock.GetUtcNow().AddMinutes(1)), Owner);
        await service.ApproveAsync(shortLived.Id, Owner);
        clock.Advance(TimeSpan.FromMinutes(2));

        Assert.Empty(await service.ListAsync(Owner, MemoryScope.Session, "session-1"));
    }

    [Fact]
    public async Task UnsafeMemoryContentShouldBeRejectedBeforeProposalIsSaved()
    {
        var (service, _) = CreateService();

        var exception = await Assert.ThrowsAsync<MemoryWorkflowException>(() => service.ProposeAsync(
            new ProposeMemoryCommand(MemoryScope.LongTermFact, "credential", "sk_live_1234567890abcdef"), Owner));

        Assert.Equal("MEMORY_SECRET_FORBIDDEN", exception.Code);
        Assert.Empty(await service.ListAsync(Owner));
    }

    [Fact]
    public async Task RetentionExtensionShouldRequireANewApprovedProposal()
    {
        var (service, clock) = CreateService();
        var proposal = await service.ProposeAsync(new ProposeMemoryCommand(MemoryScope.UserPreference, "format", "表格",
            ExpiresAt: clock.GetUtcNow().AddDays(30)), Owner);
        var memory = await service.ApproveAsync(proposal.Id, Owner);

        var exception = await Assert.ThrowsAsync<MemoryWorkflowException>(() => service.CorrectAsync(memory.Id,
            new CorrectMemoryCommand("列表", memory.Version, clock.GetUtcNow().AddDays(31)), Owner));

        Assert.Equal("MEMORY_RETENTION_EXTENSION_DENIED", exception.Code);
    }

    [Fact]
    public async Task DuplicateActiveKeyShouldRequireCorrectionInsteadOfCreatingAmbiguity()
    {
        var (service, _) = CreateService();
        var first = await service.ProposeAsync(
            new ProposeMemoryCommand(MemoryScope.UserPreference, "answer.format", "列表"), Owner);
        await service.ApproveAsync(first.Id, Owner);
        var duplicate = await service.ProposeAsync(
            new ProposeMemoryCommand(MemoryScope.UserPreference, "ANSWER.FORMAT", "表格"), Owner);

        var exception = await Assert.ThrowsAsync<MemoryWorkflowException>(() => service.ApproveAsync(duplicate.Id, Owner));

        Assert.Equal("MEMORY_KEY_ALREADY_EXISTS", exception.Code);
        Assert.Single(await service.ListAsync(Owner));
    }

    private static (MemoryWorkflowService Service, ManualTimeProvider Clock) CreateService()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 7, 13, 8, 0, 0, TimeSpan.Zero));
        var service = new MemoryWorkflowService(new InMemoryMemoryStore(), new RuleBasedMemoryContentSafetyService(),
            new InMemoryTraceSink(), clock, new MemoryWorkflowOptions());
        return (service, clock);
    }

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;
        public override DateTimeOffset GetUtcNow() => _utcNow;
        public void Advance(TimeSpan duration) => _utcNow = _utcNow.Add(duration);
    }
}
