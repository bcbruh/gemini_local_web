using AgentLocalWeb.Persistence;
using Microsoft.Data.Sqlite;

namespace AgentLocalWeb.Persistence.Tests;

public sealed class SqliteAppStoreTests
{
    [Fact]
    public async Task InitializeCreatesDefaultStateAndIsIdempotent()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var directory = new TemporaryDirectory();
        await using var store = new SqliteAppStore(directory.File("state.db"));

        await store.InitializeAsync(cancellationToken);
        await store.InitializeAsync(cancellationToken);
        var state = await store.GetStateAsync(cancellationToken);

        Assert.Null(state.LastWorkspaceRoot);
        Assert.Equal("ask_before_changes", state.PermissionMode);
        Assert.Null(state.ActiveConversationId);
        Assert.Equal(0, state.LatestEventSequence);
    }

    [Fact]
    public async Task WorkspaceAndEventSurviveStoreRestart()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var directory = new TemporaryDirectory();
        var databasePath = directory.File("state.db");

        await using (var first = new SqliteAppStore(databasePath))
        {
            await first.InitializeAsync(cancellationToken);
            var saved = await first.SaveWorkspaceSelectionAsync(
                @"C:\source\demo",
                """{"canonicalRoot":"C:\\source\\demo","displayName":"demo"}""",
                cancellationToken);
            Assert.Equal(1, saved.Sequence);
        }

        await using var second = new SqliteAppStore(databasePath);
        await second.InitializeAsync(cancellationToken);
        var state = await second.GetStateAsync(cancellationToken);
        var events = await second.ReadEventsAfterAsync(0, cancellationToken: cancellationToken);

        Assert.Equal(@"C:\source\demo", state.LastWorkspaceRoot);
        Assert.Equal(1, state.LatestEventSequence);
        var appEvent = Assert.Single(events);
        Assert.Equal("workspace.selected", appEvent.Type);
    }

    [Fact]
    public async Task EventsAreSequencedAndCanResumeAfterCursor()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var directory = new TemporaryDirectory();
        await using var store = new SqliteAppStore(directory.File("state.db"));
        await store.InitializeAsync(cancellationToken);

        await store.AppendEventAsync("test.first", "{}", "run-1", cancellationToken);
        await store.AppendEventAsync("test.second", "{}", "run-1", cancellationToken);
        var events = await store.ReadEventsAfterAsync(1, cancellationToken: cancellationToken);

        var appEvent = Assert.Single(events);
        Assert.Equal(2, appEvent.Sequence);
        Assert.Equal("test.second", appEvent.Type);
        Assert.Equal("run-1", appEvent.RunId);
    }

    [Fact]
    public async Task WaiterCompletesWhenANewerEventCommits()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var directory = new TemporaryDirectory();
        await using var store = new SqliteAppStore(directory.File("state.db"));
        await store.InitializeAsync(cancellationToken);
        var waiter = store.WaitForEventAfterAsync(0, cancellationToken);

        await store.AppendEventAsync(
            "test.created",
            "{}",
            cancellationToken: cancellationToken);

        await waiter.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken);
    }

    [Fact]
    public async Task ConversationAndOrderedMessagesSurviveRestart()
    {
        using var directory = new TemporaryDirectory();
        var databasePath = directory.File("state.db");
        var cancellationToken = TestContext.Current.CancellationToken;
        string conversationId;

        await using (var first = new SqliteAppStore(databasePath))
        {
            await first.InitializeAsync(cancellationToken);
            conversationId = await first.GetOrCreateActiveConversationAsync(cancellationToken);
            await first.AppendConversationMessageAsync(
                conversationId,
                "user",
                "hello",
                cancellationToken);
            await first.AppendConversationMessageAsync(
                conversationId,
                "assistant",
                "world",
                cancellationToken);
        }

        await using var second = new SqliteAppStore(databasePath);
        await second.InitializeAsync(cancellationToken);
        var state = await second.GetStateAsync(cancellationToken);
        var conversation = await second.GetActiveConversationAsync(cancellationToken);

        Assert.Equal(conversationId, state.ActiveConversationId);
        Assert.NotNull(conversation);
        Assert.Equal(["user", "assistant"], conversation.Messages.Select(x => x.Role));
        Assert.Equal(["hello", "world"], conversation.Messages.Select(x => x.Content));
        Assert.Equal([1L, 2L], conversation.Messages.Select(x => x.Ordinal));
        Assert.Equal(3, state.LatestEventSequence);
    }

    [Fact]
    public async Task StartingNewConversationChangesActiveSessionWithoutDeletingOldHistory()
    {
        using var directory = new TemporaryDirectory();
        await using var store = new SqliteAppStore(directory.File("state.db"));
        var cancellationToken = TestContext.Current.CancellationToken;
        await store.InitializeAsync(cancellationToken);
        var firstId = await store.GetOrCreateActiveConversationAsync(cancellationToken);
        await store.AppendConversationMessageAsync(
            firstId,
            "user",
            "old session content",
            cancellationToken);

        var secondId = await store.StartNewActiveConversationAsync(cancellationToken);
        var active = await store.GetActiveConversationAsync(cancellationToken);
        var events = await store.ReadEventsAfterAsync(0, cancellationToken: cancellationToken);

        Assert.NotEqual(firstId, secondId);
        Assert.NotNull(active);
        Assert.Equal(secondId, active.Id);
        Assert.Empty(active.Messages);
        Assert.Equal(2, events.Count(appEvent => appEvent.Type == "conversation.created"));
    }

    [Fact]
    public async Task ConversationMessageRejectsUnsupportedRoleAndOversizedContent()
    {
        using var directory = new TemporaryDirectory();
        await using var store = new SqliteAppStore(directory.File("state.db"));
        var cancellationToken = TestContext.Current.CancellationToken;
        await store.InitializeAsync(cancellationToken);
        var conversationId = await store.GetOrCreateActiveConversationAsync(cancellationToken);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.AppendConversationMessageAsync(
                conversationId,
                "owner",
                "hello",
                cancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.AppendConversationMessageAsync(
                conversationId,
                "user",
                new string('x', 64 * 1024 + 1),
                cancellationToken));
    }

    [Fact]
    public async Task ConversationMigrationUpgradesVersionOneDatabase()
    {
        using var directory = new TemporaryDirectory();
        var databasePath = directory.File("state.db");
        var cancellationToken = TestContext.Current.CancellationToken;
        await using (var connection = new SqliteConnection($"Data Source={databasePath}"))
        {
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE schema_migrations (
                    version INTEGER PRIMARY KEY,
                    applied_utc TEXT NOT NULL
                );
                INSERT INTO schema_migrations VALUES (1, '2026-09-29T00:00:00Z');
                CREATE TABLE app_state (
                    id INTEGER PRIMARY KEY CHECK (id = 1),
                    last_workspace_root TEXT NULL,
                    permission_mode TEXT NOT NULL
                );
                INSERT INTO app_state VALUES (1, NULL, 'ask_before_changes');
                CREATE TABLE app_events (
                    sequence INTEGER PRIMARY KEY AUTOINCREMENT,
                    occurred_utc TEXT NOT NULL,
                    event_type TEXT NOT NULL,
                    payload_json TEXT NOT NULL CHECK (json_valid(payload_json)),
                    run_id TEXT NULL
                );
                """;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var store = new SqliteAppStore(databasePath);
        await store.InitializeAsync(cancellationToken);
        var conversationId = await store.GetOrCreateActiveConversationAsync(cancellationToken);
        var state = await store.GetStateAsync(cancellationToken);

        Assert.Equal(conversationId, state.ActiveConversationId);
    }

    [Fact]
    public async Task RunTransitionsPersistWithCorrelatedEvents()
    {
        using var directory = new TemporaryDirectory();
        await using var store = new SqliteAppStore(directory.File("state.db"));
        var cancellationToken = TestContext.Current.CancellationToken;
        await store.InitializeAsync(cancellationToken);
        var conversationId = await store.GetOrCreateActiveConversationAsync(cancellationToken);

        var created = await store.CreateRunAsync(
            "run-1",
            conversationId,
            "local-agent/v1",
            "agent-system/v1",
            cancellationToken);
        var transitioned = await store.TransitionRunAsync(
            "run-1",
            "queued",
            "preparing_context",
            cancellationToken: cancellationToken);
        var events = await store.ReadEventsAfterAsync(0, cancellationToken: cancellationToken);

        Assert.Equal("queued", created.Status);
        Assert.Equal("preparing_context", transitioned.Status);
        Assert.Null(transitioned.CompletedAt);
        Assert.Contains(events, item => item.Type == "run.created" && item.RunId == "run-1");
        Assert.Contains(events, item => item.Type == "run.transitioned" && item.RunId == "run-1");
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.TransitionRunAsync(
            "run-1",
            "queued",
            "completed",
            cancellationToken: cancellationToken));
    }

    [Fact]
    public async Task InitializationMarksUnfinishedRunInterrupted()
    {
        using var directory = new TemporaryDirectory();
        var databasePath = directory.File("state.db");
        var cancellationToken = TestContext.Current.CancellationToken;
        await using (var first = new SqliteAppStore(databasePath))
        {
            await first.InitializeAsync(cancellationToken);
            var conversationId = await first.GetOrCreateActiveConversationAsync(cancellationToken);
            await first.CreateRunAsync(
                "run-abandoned",
                conversationId,
                "local-agent/v1",
                "agent-system/v1",
                cancellationToken);
        }

        await using var second = new SqliteAppStore(databasePath);
        await second.InitializeAsync(cancellationToken);
        var run = await second.GetLatestRunAsync(cancellationToken);
        var events = await second.ReadEventsAfterAsync(0, cancellationToken: cancellationToken);

        Assert.NotNull(run);
        Assert.Equal("interrupted", run.Status);
        Assert.Equal("process_interrupted", run.ErrorCategory);
        Assert.NotNull(run.CompletedAt);
        Assert.Contains(events, item => item.Type == "run.interrupted" && item.RunId == run.Id);
    }

    [Fact]
    public async Task ActiveRunCancellationRequestIsDurable()
    {
        using var directory = new TemporaryDirectory();
        await using var store = new SqliteAppStore(directory.File("state.db"));
        var cancellationToken = TestContext.Current.CancellationToken;
        await store.InitializeAsync(cancellationToken);
        var conversationId = await store.GetOrCreateActiveConversationAsync(cancellationToken);
        await store.CreateRunAsync(
            "run-cancel",
            conversationId,
            "local-agent/v1",
            "agent-system/v1",
            cancellationToken);

        Assert.True(await store.RequestRunCancellationAsync("run-cancel", cancellationToken));
        Assert.False(await store.RequestRunCancellationAsync("missing", cancellationToken));
        var run = await store.GetLatestRunAsync(cancellationToken);
        Assert.True(run?.CancelRequested);
    }

    [Fact]
    public async Task ToolCallLifecycleIsDurableAndEventsOmitArguments()
    {
        using var directory = new TemporaryDirectory();
        var databasePath = directory.File("state.db");
        var cancellationToken = TestContext.Current.CancellationToken;
        await using (var first = new SqliteAppStore(databasePath))
        {
            await first.InitializeAsync(cancellationToken);
            var conversationId = await first.GetOrCreateActiveConversationAsync(cancellationToken);
            await first.CreateRunAsync("run-tool", conversationId, "local-agent/v1",
                "agent-system/v1", cancellationToken);
            await first.CreateToolCallAsync("run-tool", "request-1", "read_file", cancellationToken);
            await Assert.ThrowsAnyAsync<Exception>(() => first.CreateToolCallAsync(
                "run-tool", "request-1", "read_file", cancellationToken));
            var finished = await first.FinishToolCallAsync(
                "run-tool", "request-1", "succeeded", cancellationToken: cancellationToken);
            Assert.Equal("succeeded", finished.Status);
            Assert.NotNull(finished.CompletedAt);
            await Assert.ThrowsAsync<InvalidOperationException>(() => first.FinishToolCallAsync(
                "run-tool", "request-1", "failed", cancellationToken: cancellationToken));
        }

        await using var second = new SqliteAppStore(databasePath);
        await second.InitializeAsync(cancellationToken);
        var calls = await second.ReadToolCallsForRunAsync("run-tool", cancellationToken);
        var call = Assert.Single(calls);
        Assert.Equal("request-1", call.RequestId);
        Assert.Equal("read_file", call.Tool);
        Assert.Equal("succeeded", call.Status);
        var events = await second.ReadEventsAfterAsync(0, cancellationToken: cancellationToken);
        Assert.Contains(events, item => item.Type == "tool.requested");
        Assert.Contains(events, item => item.Type == "tool.finished");
        Assert.DoesNotContain(events, item => item.PayloadJson.Contains("request-1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task StartupInterruptsPendingToolCallWithItsRun()
    {
        using var directory = new TemporaryDirectory();
        var databasePath = directory.File("state.db");
        var cancellationToken = TestContext.Current.CancellationToken;
        await using (var first = new SqliteAppStore(databasePath))
        {
            await first.InitializeAsync(cancellationToken);
            var conversationId = await first.GetOrCreateActiveConversationAsync(cancellationToken);
            await first.CreateRunAsync("run-pending-tool", conversationId, "local-agent/v1",
                "agent-system/v1", cancellationToken);
            await first.CreateToolCallAsync("run-pending-tool", "request-pending", "search_text",
                cancellationToken);
        }

        await using var second = new SqliteAppStore(databasePath);
        await second.InitializeAsync(cancellationToken);
        var call = Assert.Single(await second.ReadToolCallsForRunAsync(
            "run-pending-tool", cancellationToken));
        Assert.Equal("interrupted", call.Status);
        Assert.Equal("process_interrupted", call.ErrorCode);
        Assert.NotNull(call.CompletedAt);
    }

    [Fact]
    public async Task ApprovalDecisionIsBoundToActionHashAndPermissionModeIsDurable()
    {
        using var directory = new TemporaryDirectory();
        var databasePath = directory.File("state.db");
        var cancellationToken = TestContext.Current.CancellationToken;
        await using (var first = new SqliteAppStore(databasePath))
        {
            await first.InitializeAsync(cancellationToken);
            var conversationId = await first.GetOrCreateActiveConversationAsync(cancellationToken);
            await first.CreateRunAsync("run-approval", conversationId, "local-agent/v1",
                "agent-system/v1", cancellationToken);
            var approval = await first.CreateApprovalAsync(
                "run-approval",
                "apply-1",
                "apply_patch",
                new string('a', 64),
                "Apply one file",
                "--- a/file.txt\n+++ b/file.txt",
                cancellationToken);

            await Assert.ThrowsAsync<InvalidOperationException>(() => first.DecideApprovalAsync(
                approval.Id,
                new string('b', 64),
                "approved",
                cancellationToken));
            Assert.NotNull(await first.GetPendingApprovalAsync(cancellationToken));
            var decided = await first.DecideApprovalAsync(
                approval.Id,
                approval.ActionHash,
                "rejected",
                cancellationToken);
            Assert.Equal("rejected", decided.Status);
            Assert.Null(await first.GetPendingApprovalAsync(cancellationToken));
            await first.SetPermissionModeAsync("auto_edit_workspace", cancellationToken);
        }

        await using var second = new SqliteAppStore(databasePath);
        await second.InitializeAsync(cancellationToken);
        Assert.Equal("auto_edit_workspace", (await second.GetStateAsync(cancellationToken)).PermissionMode);
        var events = await second.ReadEventsAfterAsync(0, cancellationToken: cancellationToken);
        Assert.Contains(events, item => item.Type == "approval.requested");
        Assert.Contains(events, item => item.Type == "approval.decided");
        Assert.Contains(events, item => item.Type == "settings.permission_changed");
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("not-json")]
    public async Task RejectsInvalidEventPayload(string payload)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var directory = new TemporaryDirectory();
        await using var store = new SqliteAppStore(directory.File("state.db"));
        await store.InitializeAsync(cancellationToken);

        await Assert.ThrowsAnyAsync<Exception>(
            () => store.AppendEventAsync(
                "test.invalid",
                payload,
                cancellationToken: cancellationToken));
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
