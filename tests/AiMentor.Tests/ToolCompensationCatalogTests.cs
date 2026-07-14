using System.Text.Json;
using AiMentor.Application;
using AiMentor.Domain;
using AiMentor.Infrastructure;
using Xunit;

namespace AiMentor.Tests;

public sealed class ToolCompensationCatalogTests
{
    [Fact]
    public void DescribeClassifiesReadOnlyAndUnsupportedMutationTools()
    {
        var registry = new ServerToolRegistry([
            new TestTool("knowledge.stats", ToolOperationRisk.ReadOnly, false),
            new TestTool("profile.update", ToolOperationRisk.Mutation, true)
        ]);
        var catalog = new ToolCompensationCatalog(registry);

        Assert.True(catalog.TryDescribe("knowledge.stats", out var readOnly));
        Assert.Equal(ToolCompensationCapability.NotApplicable, readOnly!.Capability);
        Assert.Equal("TOOL_COMPENSATION_NOT_APPLICABLE", readOnly.Code);

        Assert.True(catalog.TryDescribe("profile.update", out var mutation));
        Assert.Equal(ToolCompensationCapability.NotSupported, mutation!.Capability);
        Assert.Equal("TOOL_COMPENSATION_NOT_SUPPORTED", mutation.Code);
    }

    [Fact]
    public void DescribeRequiresManualReconciliationWhenDeleteSnapshotIsNotRetained()
    {
        var catalog = new ToolCompensationCatalog(new ServerToolRegistry([
            new ManualReconciliationTestTool("memory.delete")
        ]));

        Assert.True(catalog.TryDescribe("MEMORY.DELETE", out var descriptor));
        Assert.Equal(ToolCompensationCapability.ManualReconciliation, descriptor!.Capability);
        Assert.Equal("TOOL_COMPENSATION_SOURCE_NOT_RETAINED", descriptor.Code);
        Assert.Null(descriptor.CompensationToolName);
    }

    [Fact]
    public void DescribeExposesOnlyTheDeclaredCompensationContract()
    {
        var catalog = new ToolCompensationCatalog(new ServerToolRegistry([
            new CompensableTestTool("settings.update", "settings.restore")
        ]));

        Assert.True(catalog.TryDescribe("settings.update", out var descriptor));
        Assert.Equal(ToolCompensationCapability.Compensable, descriptor!.Capability);
        Assert.Equal("TOOL_COMPENSATION_CONTRACT_AVAILABLE", descriptor.Code);
        Assert.Equal("settings.restore", descriptor.CompensationToolName);
    }

    [Fact]
    public void DescribeReturnsFalseForUnknownOrBlankToolNames()
    {
        var catalog = new ToolCompensationCatalog(new ServerToolRegistry([]));

        Assert.False(catalog.TryDescribe("missing.tool", out var unknown));
        Assert.False(catalog.TryDescribe(" ", out var blank));
        Assert.Null(unknown);
        Assert.Null(blank);
    }

    [Fact]
    public void RegistryRejectsInvalidCompensationContracts()
    {
        var readOnly = new CompensableTestTool("report.read", "report.undo", ToolOperationRisk.ReadOnly);
        var sameName = new CompensableTestTool("settings.update", "SETTINGS.UPDATE");
        var manualReadOnly = new ManualReconciliationTestTool("report.read", ToolOperationRisk.ReadOnly);
        var conflicting = new ConflictingCompensationTestTool();

        Assert.Contains("只读工具不得声明补偿契约",
            Assert.Throws<InvalidOperationException>(() => new ServerToolRegistry([readOnly])).Message);
        Assert.Contains("补偿工具必须使用独立且非空的服务器工具名",
            Assert.Throws<InvalidOperationException>(() => new ServerToolRegistry([sameName])).Message);
        Assert.Contains("只读工具不得声明人工补偿对账",
            Assert.Throws<InvalidOperationException>(() => new ServerToolRegistry([manualReadOnly])).Message);
        Assert.Contains("不能同时声明自动补偿和人工补偿对账",
            Assert.Throws<InvalidOperationException>(() => new ServerToolRegistry([conflicting])).Message);
    }

    /// <summary>提供不具备补偿能力的最小服务器工具，供能力分类测试使用。</summary>
    private class TestTool(string name, ToolOperationRisk risk, bool requiresIdempotencyKey) : IServerTool
    {
        public ToolDescriptor Descriptor { get; } = new(name, "test", risk, TimeSpan.FromSeconds(1), 1024,
            requiresIdempotencyKey);

        public SafetyDecision ValidateArguments(JsonElement arguments) => SafetyDecision.Allowed;

        public Task<JsonElement> ExecuteAsync(ToolExecutionContext context, JsonElement arguments,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(JsonSerializer.SerializeToElement(new { ok = true }));
    }

    /// <summary>模拟显式保存正向快照并提供独立反向工具名的可补偿工具。</summary>
    private class CompensableTestTool(
        string name,
        string compensationToolName,
        ToolOperationRisk risk = ToolOperationRisk.Mutation)
        : TestTool(name, risk, risk != ToolOperationRisk.ReadOnly), ICompensableServerTool
    {
        public string CompensationToolName { get; } = compensationToolName;

        public Task<JsonElement> CaptureCompensationStateAsync(ToolExecutionContext context, JsonElement arguments,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(JsonSerializer.SerializeToElement(new { previousVersion = 1 }));

        public Task<JsonElement> CompensateAsync(ToolExecutionContext context, JsonElement compensationState,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(JsonSerializer.SerializeToElement(new { restored = true }));
    }

    /// <summary>模拟缺少安全反向材料、只能进入人工对账流程的修改工具。</summary>
    private sealed class ManualReconciliationTestTool(
        string name,
        ToolOperationRisk risk = ToolOperationRisk.Mutation)
        : TestTool(name, risk, risk != ToolOperationRisk.ReadOnly), IManualReconciliationServerTool
    {
        public string CompensationUnavailableCode => "TOOL_COMPENSATION_SOURCE_NOT_RETAINED";
        public string CompensationUnavailableExplanation => "测试工具没有保留正向执行前快照。";
    }

    /// <summary>模拟互相矛盾的自动补偿与人工对账声明，验证注册阶段失败关闭。</summary>
    private sealed class ConflictingCompensationTestTool()
        : CompensableTestTool("settings.update", "settings.restore"), IManualReconciliationServerTool
    {
        public string CompensationUnavailableCode => "TEST_MANUAL_RECONCILIATION";
        public string CompensationUnavailableExplanation => "测试冲突声明。";
    }
}
