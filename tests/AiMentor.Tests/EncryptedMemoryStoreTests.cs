using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using AiMentor.Application;
using AiMentor.Domain;
using AiMentor.Infrastructure;
using Microsoft.Extensions.AI;
using Xunit;

namespace AiMentor.Tests;

public sealed class EncryptedMemoryStoreTests
{
    private static readonly AccessContext Owner = AccessContext.Create("tenant-a", "user-a", ["all-rnd"]);
    private static readonly byte[] Key = Enumerable.Range(1, 32).Select(value => (byte)value).ToArray();

    [Fact]
    public async Task ApprovedMemoryShouldSurviveStoreRestartWithoutPlaintextAtRest()
    {
        var directory = CreateTemporaryDirectory();
        var path = Path.Combine(directory, "memory.json");
        try
        {
            var clock = new ManualTimeProvider(new DateTimeOffset(2026, 7, 13, 8, 0, 0, TimeSpan.Zero));
            string memoryId;
            using (var firstStore = CreateStore(path))
            {
                var service = CreateWorkflow(firstStore, clock);
                var proposal = await service.ProposeAsync(
                    new ProposeMemoryCommand(MemoryScope.UserPreference, "answer.format", "表格"), Owner);
                memoryId = (await service.ApproveAsync(proposal.Id, Owner)).Id;
            }

            var persisted = await File.ReadAllTextAsync(path);
            Assert.DoesNotContain("answer.format", persisted, StringComparison.Ordinal);
            Assert.DoesNotContain("表格", persisted, StringComparison.Ordinal);
            Assert.Contains("v1.", persisted, StringComparison.Ordinal);

            using var restartedStore = CreateStore(path);
            var visible = await restartedStore.ListActiveAsync(Owner, null, null, clock.GetUtcNow());
            var memory = Assert.Single(visible);
            Assert.Equal(memoryId, memory.Id);
            Assert.Equal("answer.format", memory.Key);
            Assert.Equal("表格", memory.Value);
            Assert.Empty(await restartedStore.ListActiveAsync(
                AccessContext.Create("tenant-b", "user-a", ["all-rnd"]), null, null, clock.GetUtcNow()));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task ContextProviderShouldSelectPreferencesRelevantFactsAndMatchingSessionOnly()
    {
        var store = new InMemoryMemoryStore();
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 7, 13, 8, 0, 0, TimeSpan.Zero));
        var workflow = CreateWorkflow(store, clock);
        await ApproveAsync(workflow, MemoryScope.UserPreference, "answer.format", "表格");
        await ApproveAsync(workflow, MemoryScope.LongTermFact, "project.name", "OrionOrder");
        await ApproveAsync(workflow, MemoryScope.Session, "current.task", "排查 OrionOrder", "session-a");
        await ApproveAsync(workflow, MemoryScope.Session, "current.task", "另一个会话", "session-b");
        var provider = new SafeMemoryContextProvider(store, new RuleBasedMemoryContentSafetyService(),
            new MemoryContextOptions(), clock);

        var context = await provider.GetRelevantAsync("OrionOrder 当前排查到哪里？", Owner, "session-a");

        Assert.Contains(context, item => item.Scope == MemoryScope.UserPreference && item.Value == "表格");
        Assert.Contains(context, item => item.Scope == MemoryScope.LongTermFact && item.Value == "OrionOrder");
        Assert.Contains(context, item => item.Scope == MemoryScope.Session && item.Value == "排查 OrionOrder");
        Assert.DoesNotContain(context, item => item.Value == "另一个会话");
    }

    [Fact]
    public async Task MovingCiphertextToAnotherTenantShouldFailAuthentication()
    {
        var directory = CreateTemporaryDirectory();
        var path = Path.Combine(directory, "memory.json");
        try
        {
            var clock = new ManualTimeProvider(new DateTimeOffset(2026, 7, 13, 8, 0, 0, TimeSpan.Zero));
            using (var store = CreateStore(path))
            {
                var workflow = CreateWorkflow(store, clock);
                await ApproveAsync(workflow, MemoryScope.UserPreference, "answer.format", "表格");
            }
            var tampered = (await File.ReadAllTextAsync(path)).Replace("tenant-a", "tenant-b", StringComparison.Ordinal);
            await File.WriteAllTextAsync(path, tampered);

            using var reopened = CreateStore(path);
            var attacker = AccessContext.Create("tenant-b", "user-a", ["all-rnd"]);
            await Assert.ThrowsAsync<AuthenticationTagMismatchException>(() =>
                reopened.ListActiveAsync(attacker, null, null, clock.GetUtcNow()));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task ComposerShouldMarkMemoryAsDataOnlyAndKeepItOutsideEvidence()
    {
        using var chatClient = new CapturingChatClient();
        var composer = new AgentFrameworkAnswerComposer(chatClient);
        var chunk = new KnowledgeChunk("c1", "d1", "v1", "标题", "章节", "证据正文", "tenant-a",
            new HashSet<string>(["all-rnd"]), "source.md");

        await composer.ComposeAsync("问题", [new Evidence(chunk, 1)],
            [new MemoryContextItem(MemoryScope.UserPreference, "answer.format", "表格", DateTimeOffset.UtcNow)]);

        Assert.Contains("<memory_context trust=\"data-only\">", chatClient.LastPrompt, StringComparison.Ordinal);
        Assert.Contains("\"answer.format\"", chatClient.LastPrompt, StringComparison.Ordinal);
        Assert.Contains("</memory_context>\n<evidence>", chatClient.LastPrompt.ReplaceLineEndings("\n"),
            StringComparison.Ordinal);
    }

    private static EncryptedFileMemoryStore CreateStore(string path) => new(
        new EncryptedFileMemoryStoreOptions { FilePath = path }, new AesGcmMemoryCipher(Key));

    private static MemoryWorkflowService CreateWorkflow(IMemoryStore store, TimeProvider clock) => new(store,
        new RuleBasedMemoryContentSafetyService(), new InMemoryTraceSink(), clock, new MemoryWorkflowOptions());

    private static async Task ApproveAsync(MemoryWorkflowService workflow, MemoryScope scope, string key, string value,
        string? sessionId = null)
    {
        var proposal = await workflow.ProposeAsync(new ProposeMemoryCommand(scope, key, value, sessionId), Owner);
        await workflow.ApproveAsync(proposal.Id, Owner);
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"aimentor-memory-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class CapturingChatClient : IChatClient
    {
        private static readonly ChatClientMetadata Metadata = new("AiMentor.Tests", defaultModelId: "capture");
        public string LastPrompt { get; private set; } = string.Empty;

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            _ = options;
            cancellationToken.ThrowIfCancellationRequested();
            LastPrompt = messages.Last().Text ?? string.Empty;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "已回答")));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var response = await GetResponseAsync(messages, options, cancellationToken);
            yield return new ChatResponseUpdate(ChatRole.Assistant, response.Text);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceKey is null && serviceType.IsInstanceOfType(Metadata) ? Metadata : null;

        public void Dispose() { }
    }
}
