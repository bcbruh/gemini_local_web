using System.Text.Json;
using AgentLocalWeb.Agent.Core;
using AgentLocalWeb.Brain;
using AgentLocalWeb.Brain.Fake;
using AgentLocalWeb.Persistence;

namespace AgentLocalWeb.AppHost.Tests;

public sealed class ConversationServiceTests
{
    [Fact]
    public async Task PersistsUserAndFinalAssistantMessages()
    {
        using var directory = new TemporaryDirectory();
        await using var store = new SqliteAppStore(directory.File("state.db"));
        var cancellationToken = TestContext.Current.CancellationToken;
        await store.InitializeAsync(cancellationToken);
        await using var brain = new FakeBrain([
            Final("fake answer")
        ]);
        await brain.ConnectAsync(
            new BrainConnectionRequest(Interactive: false),
            cancellationToken);
        using var engine = new AgentRunEngine(
            brain,
            new NoTools(),
            eventSink: new SqliteAgentRunEventSink(store));
        using var service = new ConversationService(store, brain, engine);
        await service.InitializeAsync(cancellationToken);

        var conversation = await service.SendAsync("question", cancellationToken);

        Assert.Equal(["user", "assistant"], conversation.Messages.Select(x => x.Role));
        Assert.Equal(["question", "fake answer"], conversation.Messages.Select(x => x.Content));
        var persisted = await store.GetActiveConversationAsync(cancellationToken);
        Assert.NotNull(persisted);
        Assert.Equal(conversation.Id, persisted.Id);
        Assert.Equal(
            conversation.Messages.Select(x => (x.Role, x.Content)),
            persisted.Messages.Select(x => (x.Role, x.Content)));
    }

    [Fact]
    public async Task DisconnectedBrainDoesNotPersistUserMessage()
    {
        using var directory = new TemporaryDirectory();
        await using var store = new SqliteAppStore(directory.File("state.db"));
        var cancellationToken = TestContext.Current.CancellationToken;
        await store.InitializeAsync(cancellationToken);
        await using var brain = new FakeBrain();
        using var engine = new AgentRunEngine(
            brain,
            new NoTools(),
            eventSink: new SqliteAgentRunEventSink(store));
        using var service = new ConversationService(store, brain, engine);
        await service.InitializeAsync(cancellationToken);

        await Assert.ThrowsAsync<BrainException>(
            () => service.SendAsync("question", cancellationToken));

        var conversation = await store.GetActiveConversationAsync(cancellationToken);
        Assert.NotNull(conversation);
        Assert.Empty(conversation.Messages);
    }

    [Fact]
    public async Task NewSessionExcludesPreviousConversationFromBrainContext()
    {
        using var directory = new TemporaryDirectory();
        await using var store = new SqliteAppStore(directory.File("state.db"));
        var cancellationToken = TestContext.Current.CancellationToken;
        await store.InitializeAsync(cancellationToken);
        await using var brain = new FakeBrain([
            Final("first answer"),
            Final("second answer")
        ]);
        await brain.ConnectAsync(
            new BrainConnectionRequest(Interactive: false),
            cancellationToken);
        using var engine = new AgentRunEngine(
            brain,
            new NoTools(),
            eventSink: new SqliteAgentRunEventSink(store));
        using var service = new ConversationService(store, brain, engine);
        await service.InitializeAsync(cancellationToken);

        var first = await service.SendAsync("first question", cancellationToken);
        var second = await service.StartNewAsync(cancellationToken);
        var completedSecond = await service.SendAsync("second question", cancellationToken);

        Assert.NotEqual(first.Id, second.Id);
        Assert.Empty(second.Messages);
        Assert.Equal(["second question", "second answer"],
            completedSecond.Messages.Select(message => message.Content));
        Assert.Equal(2, brain.ReceivedRequests.Count);
        var secondRequest = brain.ReceivedRequests.ElementAt(1);
        Assert.DoesNotContain(
            secondRequest.Messages,
            message => message.Content.Contains("first question", StringComparison.Ordinal) ||
                       message.Content.Contains("first answer", StringComparison.Ordinal));
    }

    private static BrainResponse.Final Final(string message) => new(
        "response-1",
        JsonSerializer.Serialize(new
        {
            protocol = ProtocolV1.Version,
            type = "final",
            message
        }));

    private sealed class NoTools : IToolDispatcher
    {
        public IReadOnlySet<string> SupportedTools { get; } = new HashSet<string>();

        public ValueTask<ToolExecutionResult> ExecuteAsync(
            ProtocolResponse.ToolRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "AgentLocalWeb.Tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string File(string name) => System.IO.Path.Combine(Path, name);

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
