using AiMentor.Application;
using AiMentor.Domain;
using AiMentor.Infrastructure;
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

    private static ToolExecutionReconciliationService CreateService(InMemoryToolExecutionLedger ledger) =>
        new(ledger, new InMemoryTraceSink(), new ToolExecutionReconciliationOptions(), TimeProvider.System);

    private static async Task AddUnknownAsync(InMemoryToolExecutionLedger ledger, char marker, string tenantId)
    {
        var key = new string(marker, 64);
        var acquired = await ledger.TryAcquireAsync(new ToolExecutionLedgerRequest(key, new string(marker, 64),
            $"run-{marker}", tenantId, "subject-a", "memory.delete"), TimeSpan.FromSeconds(30),
            TimeSpan.FromHours(1), 100);
        await ledger.MarkExecutingAsync(key, acquired.LeaseToken!);
        await ledger.MarkOutcomeUnknownAsync(key, acquired.LeaseToken!);
    }
}
