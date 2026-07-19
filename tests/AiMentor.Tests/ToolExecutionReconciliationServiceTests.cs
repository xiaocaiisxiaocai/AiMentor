using AiMentor.Application;
using AiMentor.Domain;
using AiMentor.Infrastructure;
using System.Text.Json;
using Xunit;

namespace AiMentor.Tests;

public sealed class ToolExecutionReconciliationServiceTests
{
    [Fact]
    public async Task ReconcilerShouldOnlySeeCurrentTenantRecords()
    {
        var ledger = new InMemoryToolExecutionLedger(TimeProvider.System);
        await AddUnknownAsync(ledger, 'A', "tenant-a");
        await AddUnknownAsync(ledger, 'B', "tenant-b");
        var service = CreateService(ledger);

        var records = await service.ListOutcomeUnknownAsync(
            AccessContext.Create("tenant-a", "reconciler-a", ["tool-reconcilers"]), 50);

        Assert.Equal("tenant-a", Assert.Single(records).TenantId);
    }

    [Fact]
    public async Task CallerWithoutReconcilerRoleShouldBeForbidden()
    {
        var service = CreateService(new InMemoryToolExecutionLedger(TimeProvider.System));

        var exception = await Assert.ThrowsAsync<ToolExecutionReconciliationException>(() =>
            service.ListOutcomeUnknownAsync(AccessContext.Create("tenant-a", "reader-a", ["readers"]), 50));

        Assert.Equal(ToolExecutionReconciliationErrorKind.Forbidden, exception.Kind);
        Assert.Equal("TOOL_RECONCILER_ROLE_REQUIRED", exception.Code);
    }

    [Fact]
    public async Task SignedKeysetCursorShouldReturnEveryRecordAndRejectTamperingOrTenantReuse()
    {
        var now = new DateTimeOffset(2026, 7, 16, 1, 0, 0, TimeSpan.Zero);
        var ledger = new InMemoryToolExecutionLedger(new FixedTimeProvider(now));
        var expectedKeys = Enumerable.Range(0, 205).Select(index =>
            index.ToString("X64", System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        foreach (var key in expectedKeys)
            await AddUnknownAsync(ledger, key, "tenant-a");
        var options = new ToolExecutionReconciliationOptions
        {
            MaximumPageSize = 100,
            MaximumOperationsScan = 500,
            CursorSigningKey = Enumerable.Repeat((byte)0x5A, 32).ToArray()
        };
        var service = new ToolExecutionReconciliationService(
            ledger, new InMemoryTraceSink(), options, new FixedTimeProvider(now), []);
        var access = AccessContext.Create("tenant-a", "reconciler-a", ["tool-reconcilers"]);

        var first = await service.ListOutcomeUnknownPageAsync(access, 100);
        var all = new List<OutcomeUnknownToolExecution>(first.Items);
        var cursor = first.NextCursor;
        while (cursor is not null)
        {
            var page = await service.ListOutcomeUnknownPageAsync(access, 100, cursor);
            all.AddRange(page.Items);
            cursor = page.NextCursor;
        }
        var tampered = first.NextCursor![..^1]
                       + (first.NextCursor[^1] == 'A' ? 'B' : 'A');
        var tamperFailure = await Assert.ThrowsAsync<ToolExecutionReconciliationException>(() =>
            service.ListOutcomeUnknownPageAsync(access, 100, tampered));
        var tenantFailure = await Assert.ThrowsAsync<ToolExecutionReconciliationException>(() =>
            service.ListOutcomeUnknownPageAsync(
                AccessContext.Create("tenant-b", "reconciler-b", ["tool-reconcilers"]), 100,
                first.NextCursor));

        Assert.Equal(expectedKeys, all.Select(item => item.ExecutionKey));
        Assert.Equal(205, all.Select(item => item.ExecutionKey).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal("TOOL_RECONCILIATION_CURSOR_INVALID", tamperFailure.Code);
        Assert.Equal("TOOL_RECONCILIATION_CURSOR_INVALID", tenantFailure.Code);
        var encodedEnvelope = first.NextCursor!.Replace('-', '+').Replace('_', '/');
        encodedEnvelope += new string('=', (4 - encodedEnvelope.Length % 4) % 4);
        var envelope = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(encodedEnvelope));
        Assert.DoesNotContain("tenant-a", envelope, StringComparison.Ordinal);
        Assert.DoesNotContain("subject-a", envelope, StringComparison.Ordinal);
        Assert.DoesNotContain("memory.delete", envelope, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MemoryDeleteProbeShouldVerifyFingerprintAndReportTargetState()
    {
        var ledger = new InMemoryToolExecutionLedger(TimeProvider.System);
        var memoryStore = new InMemoryMemoryStore();
        var owner = AccessContext.Create("tenant-a", "subject-a", []);
        var now = DateTimeOffset.UtcNow;
        var proposal = new MemoryProposal("proposal-a", owner.TenantId, owner.SubjectId,
            MemoryScope.UserPreference, null, "format", "short", now, now.AddMinutes(5),
            now.AddHours(1), MemoryProposalStatus.PendingApproval);
        await memoryStore.SaveProposalAsync(proposal);
        var memory = (await memoryStore.ApproveAsync(proposal.Id, owner, now)).Value!;
        var arguments = JsonSerializer.SerializeToElement(new
        {
            memoryId = memory.Id,
            expectedVersion = memory.Version
        });
        var key = new string('C', 64);
        var fingerprint = JsonArgumentFingerprint.Create("memory.delete", arguments, owner);
        var acquired = await ledger.TryAcquireAsync(new ToolExecutionLedgerRequest(key, fingerprint, "run-C",
            owner.TenantId, owner.SubjectId, "memory.delete"), TimeSpan.FromSeconds(30), TimeSpan.FromHours(1), 100);
        await ledger.MarkExecutingAsync(key, acquired.LeaseToken!);
        await ledger.MarkOutcomeUnknownAsync(key, acquired.LeaseToken!);
        var service = CreateService(ledger, memoryStore);
        var reconciler = AccessContext.Create("tenant-a", "reconciler-a", ["tool-reconcilers"]);

        var beforeDelete = await service.ProbeOutcomeAsync(reconciler, key, arguments);
        await memoryStore.DeleteAsync(memory.Id, owner, memory.Version);
        var afterDelete = await service.ProbeOutcomeAsync(reconciler, key, arguments);
        var wrongArguments = JsonSerializer.SerializeToElement(new { memoryId = "other", expectedVersion = 1 });
        var mismatch = await Assert.ThrowsAsync<ToolExecutionReconciliationException>(() =>
            service.ProbeOutcomeAsync(reconciler, key, wrongArguments));

        Assert.Equal(ToolOutcomeProbeState.NotApplied, beforeDelete.State);
        Assert.Equal(ToolOutcomeProbeState.Applied, afterDelete.State);
        Assert.Equal(key, afterDelete.ExecutionKey);
        Assert.Equal("TOOL_RECONCILIATION_ARGUMENTS_MISMATCH", mismatch.Code);
    }

    [Fact]
    public async Task ForeignMemoryTargetMustNotBeReportedAsApplied()
    {
        var ledger = new InMemoryToolExecutionLedger(TimeProvider.System);
        var memoryStore = new InMemoryMemoryStore();
        var foreignOwner = AccessContext.Create("tenant-b", "subject-b", []);
        var now = DateTimeOffset.UtcNow;
        var proposal = new MemoryProposal("proposal-b", foreignOwner.TenantId, foreignOwner.SubjectId,
            MemoryScope.UserPreference, null, "format", "long", now, now.AddMinutes(5),
            now.AddHours(1), MemoryProposalStatus.PendingApproval);
        await memoryStore.SaveProposalAsync(proposal);
        var foreignMemory = (await memoryStore.ApproveAsync(proposal.Id, foreignOwner, now)).Value!;
        var originalOwner = AccessContext.Create("tenant-a", "subject-a", []);
        var arguments = JsonSerializer.SerializeToElement(new
        {
            memoryId = foreignMemory.Id,
            expectedVersion = foreignMemory.Version
        });
        var key = new string('D', 64);
        var fingerprint = JsonArgumentFingerprint.Create("memory.delete", arguments, originalOwner);
        var acquired = await ledger.TryAcquireAsync(new ToolExecutionLedgerRequest(key, fingerprint, "run-D",
            originalOwner.TenantId, originalOwner.SubjectId, "memory.delete"), TimeSpan.FromSeconds(30),
            TimeSpan.FromHours(1), 100);
        await ledger.MarkExecutingAsync(key, acquired.LeaseToken!);
        await ledger.MarkOutcomeUnknownAsync(key, acquired.LeaseToken!);

        var result = await CreateService(ledger, memoryStore).ProbeOutcomeAsync(
            AccessContext.Create("tenant-a", "reconciler-a", ["tool-reconcilers"]), key, arguments);

        Assert.Equal(ToolOutcomeProbeState.Indeterminate, result.State);
        Assert.Equal("MEMORY_DELETE_TARGET_INACCESSIBLE", result.Code);
    }

    private static ToolExecutionReconciliationService CreateService(InMemoryToolExecutionLedger ledger,
        InMemoryMemoryStore? memoryStore = null) => new(ledger, new InMemoryTraceSink(),
            new ToolExecutionReconciliationOptions(), TimeProvider.System,
            [new MemoryDeleteOutcomeProbe(memoryStore ?? new InMemoryMemoryStore(), TimeProvider.System)]);

    private static async Task AddUnknownAsync(InMemoryToolExecutionLedger ledger, char marker, string tenantId)
        => await AddUnknownAsync(ledger, new string(marker, 64), tenantId);

    private static async Task AddUnknownAsync(InMemoryToolExecutionLedger ledger, string key, string tenantId)
    {
        var acquired = await ledger.TryAcquireAsync(new ToolExecutionLedgerRequest(key, new string('A', 64),
            $"run-{key[..8]}", tenantId, "subject-a", "memory.delete"), TimeSpan.FromSeconds(30),
            TimeSpan.FromHours(1), 500);
        await ledger.MarkExecutingAsync(key, acquired.LeaseToken!);
        await ledger.MarkOutcomeUnknownAsync(key, acquired.LeaseToken!);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
