using System.Text.Json;
using AgentLocalWeb.Agent.Core;
using AgentLocalWeb.Brain;
using AgentLocalWeb.Brain.Fake;

namespace AgentLocalWeb.Agent.Core.Tests;

public sealed class AgentRunEngineTests
{
    [Fact]
    public async Task RunsToolObservationLoopToFinalResponse()
    {
        await using var brain = new FakeBrain(
            [
                ToolRequest("read-1", "read_file", """{"path":"src/app.cs"}"""),
                Final("The file contains an app.")
            ]);
        var tools = new RecordingTools();
        var events = new RecordingEventSink();
        var toolCalls = new RecordingToolCallSink();
        using var engine = new AgentRunEngine(
            brain, tools, eventSink: events, toolCallSink: toolCalls);
        var cancellationToken = TestContext.Current.CancellationToken;
        await brain.ConnectAsync(new BrainConnectionRequest(false), cancellationToken);

        var result = await engine.RunAsync(
            new AgentRunRequest("conversation-1", "What is in app.cs?"),
            cancellationToken);

        Assert.Equal(RunState.Completed, result.State);
        Assert.Equal("The file contains an app.", result.FinalMessage);
        Assert.Equal(1, tools.ExecutionCount);
        var secondRequest = Assert.Single(brain.ReceivedRequests.Skip(1));
        var observation = Assert.Single(
            secondRequest.Messages,
            message => message.Role == BrainRole.Tool);
        Assert.Equal("read-1", observation.ToolCallId);
        Assert.Contains("tool_result", observation.Content, StringComparison.Ordinal);
        Assert.Contains("file content", observation.Content, StringComparison.Ordinal);
        Assert.Equal(RunState.Completed, events.Transitions[^1].Current);
        Assert.Equal(
            [AgentToolCallStatus.Requested, AgentToolCallStatus.Succeeded],
            toolCalls.Events.Select(item => item.Status));
    }

    [Fact]
    public async Task RetriesMalformedProtocolWithinBudget()
    {
        await using var brain = new FakeBrain(
            [new BrainResponse.Final("bad", "plain prose"), Final("corrected")]);
        using var engine = new AgentRunEngine(brain, new RecordingTools());
        var cancellationToken = TestContext.Current.CancellationToken;
        await brain.ConnectAsync(new BrainConnectionRequest(false), cancellationToken);

        var result = await engine.RunAsync(
            new AgentRunRequest("conversation-1", "question"),
            cancellationToken);

        Assert.Equal(RunState.Completed, result.State);
        Assert.Equal("corrected", result.FinalMessage);
        Assert.Equal(2, brain.ReceivedRequests.Count);
        Assert.Contains(
            brain.ReceivedRequests.Last().Messages,
            message => message.Content.Contains("Protocol response rejected", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FailsClosedAfterProtocolRetryBudget()
    {
        await using var brain = new FakeBrain(
            [
                new BrainResponse.Final("bad-1", "bad"),
                new BrainResponse.Final("bad-2", "still bad")
            ]);
        using var engine = new AgentRunEngine(
            brain,
            new RecordingTools(),
            new AgentRunOptions(MaximumProtocolRetries: 1));
        var cancellationToken = TestContext.Current.CancellationToken;
        await brain.ConnectAsync(new BrainConnectionRequest(false), cancellationToken);

        var result = await engine.RunAsync(
            new AgentRunRequest("conversation-1", "question"),
            cancellationToken);

        Assert.Equal(RunState.Failed, result.State);
        Assert.Equal(AgentRunErrorCode.InvalidProtocol, result.Error?.Code);
    }

    [Fact]
    public async Task DuplicateToolRequestIdIsNotExecutedTwice()
    {
        await using var brain = new FakeBrain(
            [
                ToolRequest("same-id", "read_file", """{"path":"a.cs"}"""),
                ToolRequest("same-id", "read_file", """{"path":"b.cs"}"""),
                Final("done")
            ]);
        var tools = new RecordingTools();
        using var engine = new AgentRunEngine(brain, tools);
        var cancellationToken = TestContext.Current.CancellationToken;
        await brain.ConnectAsync(new BrainConnectionRequest(false), cancellationToken);

        var result = await engine.RunAsync(
            new AgentRunRequest("conversation-1", "question"),
            cancellationToken);

        Assert.Equal(RunState.Completed, result.State);
        Assert.Equal(1, tools.ExecutionCount);
    }

    [Fact]
    public async Task CancellationStopsBrainWaitAndEndsRun()
    {
        await using var brain = new FakeBrain(
            [Final("too late")],
            responseDelay: TimeSpan.FromSeconds(30));
        using var engine = new AgentRunEngine(brain, new RecordingTools());
        var testCancellation = TestContext.Current.CancellationToken;
        await brain.ConnectAsync(new BrainConnectionRequest(false), testCancellation);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(testCancellation);
        cancellation.CancelAfter(TimeSpan.FromMilliseconds(100));

        var result = await engine.RunAsync(
            new AgentRunRequest("conversation-1", "question"),
            cancellation.Token);

        Assert.Equal(RunState.Cancelled, result.State);
        Assert.Equal(AgentRunErrorCode.Cancelled, result.Error?.Code);
    }

    [Fact]
    public async Task ActiveRunCanBeCancelledExplicitly()
    {
        await using var brain = new FakeBrain(
            [Final("too late")],
            responseDelay: TimeSpan.FromSeconds(30));
        using var engine = new AgentRunEngine(brain, new RecordingTools());
        var cancellationToken = TestContext.Current.CancellationToken;
        await brain.ConnectAsync(new BrainConnectionRequest(false), cancellationToken);
        var runTask = engine.RunAsync(
            new AgentRunRequest("conversation-1", "question", "explicit-run"),
            cancellationToken);

        Assert.Equal("explicit-run", engine.ActiveRunId);
        Assert.True(engine.CancelActive());
        var result = await runTask;

        Assert.Equal(RunState.Cancelled, result.State);
        Assert.Null(engine.ActiveRunId);
        Assert.False(engine.CancelActive());
    }

    [Fact]
    public async Task CancellationDuringToolExecutionRecordsCancelledToolCall()
    {
        await using var brain = new FakeBrain(
            [ToolRequest("slow-1", "read_file", """{"path":"slow.txt"}""")]);
        var tools = new BlockingTools();
        var toolCalls = new RecordingToolCallSink();
        using var engine = new AgentRunEngine(brain, tools, toolCallSink: toolCalls);
        var cancellationToken = TestContext.Current.CancellationToken;
        await brain.ConnectAsync(new BrainConnectionRequest(false), cancellationToken);

        var runTask = engine.RunAsync(
            new AgentRunRequest("conversation-1", "Read slow.txt", "slow-run"),
            cancellationToken);
        await tools.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        Assert.True(engine.CancelActive());
        var result = await runTask;

        Assert.Equal(RunState.Cancelled, result.State);
        Assert.Equal(
            [AgentToolCallStatus.Requested, AgentToolCallStatus.Cancelled],
            toolCalls.Events.Select(item => item.Status));
    }

    [Fact]
    public async Task ContextBudgetKeepsRecentConversationAndRejectsStoredSystemMessages()
    {
        await using var brain = new FakeBrain([Final("done")]);
        using var engine = new AgentRunEngine(
            brain,
            new RecordingTools(),
            new AgentRunOptions(MaximumContextBytes: 5 * 1024));
        var cancellationToken = TestContext.Current.CancellationToken;
        await brain.ConnectAsync(new BrainConnectionRequest(false), cancellationToken);
        var oldMessage = $"old-{new string('a', 3_000)}";
        var recentMessage = $"recent-{new string('b', 3_000)}";

        var result = await engine.RunAsync(
            new AgentRunRequest(
                "conversation-1",
                "current question",
                History:
                [
                    new BrainMessage(BrainRole.System, "stored system content must not be trusted"),
                    new BrainMessage(BrainRole.User, oldMessage),
                    new BrainMessage(BrainRole.Assistant, recentMessage)
                ]),
            cancellationToken);

        Assert.Equal(RunState.Completed, result.State);
        var request = Assert.Single(brain.ReceivedRequests);
        Assert.DoesNotContain(request.Messages, message => message.Content == oldMessage);
        Assert.Contains(request.Messages, message => message.Content == recentMessage);
        Assert.DoesNotContain(
            request.Messages,
            message => message.Content.Contains("stored system content", StringComparison.Ordinal));
        Assert.Equal("current question", request.Messages[^1].Content);
    }

    [Fact]
    public async Task OversizedCurrentTurnFailsBeforeCallingBrain()
    {
        await using var brain = new FakeBrain([Final("must not be used")]);
        using var engine = new AgentRunEngine(
            brain,
            new RecordingTools(),
            new AgentRunOptions(MaximumContextBytes: 4 * 1024));
        var cancellationToken = TestContext.Current.CancellationToken;
        await brain.ConnectAsync(new BrainConnectionRequest(false), cancellationToken);

        var result = await engine.RunAsync(
            new AgentRunRequest("conversation-1", new string('x', 5_000)),
            cancellationToken);

        Assert.Equal(RunState.Failed, result.State);
        Assert.Equal(AgentRunErrorCode.ContextLimit, result.Error?.Code);
        Assert.Empty(brain.ReceivedRequests);
    }

    [Theory]
    [InlineData(BrainErrorCode.AuthenticationFailed, AgentRunErrorCode.BrainAuthentication)]
    [InlineData(BrainErrorCode.SessionExpired, AgentRunErrorCode.BrainSessionExpired)]
    [InlineData(BrainErrorCode.RateLimited, AgentRunErrorCode.BrainRateLimited)]
    [InlineData(BrainErrorCode.NetworkFailure, AgentRunErrorCode.BrainNetwork)]
    [InlineData(BrainErrorCode.CompatibilityFailure, AgentRunErrorCode.BrainCompatibility)]
    [InlineData(BrainErrorCode.InvalidResponse, AgentRunErrorCode.BrainInvalidResponse)]
    [InlineData(BrainErrorCode.Unavailable, AgentRunErrorCode.BrainUnavailable)]
    public async Task MapsBrainFailuresToActionableRunErrors(
        BrainErrorCode brainError,
        AgentRunErrorCode expectedRunError)
    {
        await using var brain = new FailingBrain(brainError);
        using var engine = new AgentRunEngine(brain, new RecordingTools());

        var result = await engine.RunAsync(
            new AgentRunRequest("conversation-1", "question"),
            TestContext.Current.CancellationToken);

        Assert.Equal(RunState.Failed, result.State);
        Assert.Equal(expectedRunError, result.Error?.Code);
        Assert.DoesNotContain("private diagnostic", result.Error?.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ToolApprovalDecision.Approved, 1, "succeeded")]
    [InlineData(ToolApprovalDecision.Rejected, 0, "user rejected")]
    public async Task ApprovalIsRequiredBeforePlannedToolExecution(
        ToolApprovalDecision decision,
        int expectedExecutions,
        string expectedObservation)
    {
        await using var brain = new FakeBrain(
            [
                ToolRequest("apply-1", "apply_patch", """{"action_hash":"abc"}"""),
                Final("done")
            ]);
        var tools = new ApprovalTools();
        var approvals = new RecordingApprovalGateway(decision);
        var events = new RecordingEventSink();
        using var engine = new AgentRunEngine(
            brain,
            tools,
            eventSink: events,
            approvalGateway: approvals);
        var cancellationToken = TestContext.Current.CancellationToken;
        await brain.ConnectAsync(new BrainConnectionRequest(false), cancellationToken);

        var result = await engine.RunAsync(
            new AgentRunRequest("conversation-1", "change it"),
            cancellationToken);

        Assert.Equal(RunState.Completed, result.State);
        Assert.Equal(expectedExecutions, tools.ExecutionCount);
        Assert.Single(approvals.Requests);
        Assert.Contains(events.Transitions, transition =>
            transition.Current == RunState.WaitingForApproval);
        var observation = Assert.Single(
            brain.ReceivedRequests.ElementAt(1).Messages,
            message => message.Role == BrainRole.Tool);
        Assert.Contains(expectedObservation, observation.Content, StringComparison.OrdinalIgnoreCase);
    }

    private static BrainResponse.ToolRequest ToolRequest(
        string id,
        string tool,
        string arguments)
    {
        using var document = JsonDocument.Parse(arguments);
        return new BrainResponse.ToolRequest(
            id,
            ProtocolV1.Version,
            tool,
            document.RootElement.Clone());
    }

    private static BrainResponse.Final Final(string message) =>
        new(
            Guid.NewGuid().ToString("N"),
            JsonSerializer.Serialize(new
            {
                protocol = ProtocolV1.Version,
                type = "final",
                message
            }));

    private sealed class RecordingTools : IToolDispatcher
    {
        public IReadOnlySet<string> SupportedTools { get; } =
            new HashSet<string>(["read_file"], StringComparer.Ordinal);

        public int ExecutionCount { get; private set; }

        public ValueTask<ToolExecutionResult> ExecuteAsync(
            ProtocolResponse.ToolRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ExecutionCount++;
            return ValueTask.FromResult(ToolExecutionResult.Success(
                request.RequestId,
                request.Tool,
                JsonSerializer.SerializeToElement(new { content = "file content" })));
        }
    }

    private sealed class RecordingEventSink : IAgentRunEventSink
    {
        public List<AgentRunTransition> Transitions { get; } = [];

        public ValueTask WriteAsync(
            AgentRunTransition transition,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Transitions.Add(transition);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ApprovalTools : IPlannedToolDispatcher
    {
        public IReadOnlySet<string> SupportedTools { get; } =
            new HashSet<string>(["apply_patch"], StringComparer.Ordinal);

        public int ExecutionCount { get; private set; }

        public ValueTask<ToolExecutionPlan> PlanAsync(
            ProtocolResponse.ToolRequest request,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new ToolExecutionPlan(Approval: new ToolApprovalDetails(
                new string('a', 64),
                "Apply one file",
                "--- a/file\n+++ b/file")));

        public ValueTask<ToolExecutionResult> ExecuteAsync(
            ProtocolResponse.ToolRequest request,
            CancellationToken cancellationToken = default)
        {
            ExecutionCount++;
            return ValueTask.FromResult(ToolExecutionResult.Success(
                request.RequestId,
                request.Tool,
                JsonSerializer.SerializeToElement(new { status = "succeeded" })));
        }
    }

    private sealed class RecordingApprovalGateway(ToolApprovalDecision decision)
        : IToolApprovalGateway
    {
        public List<ToolApprovalRequest> Requests { get; } = [];

        public ValueTask<ToolApprovalDecision> RequestAsync(
            ToolApprovalRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return ValueTask.FromResult(decision);
        }
    }

    private sealed class BlockingTools : IToolDispatcher
    {
        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlySet<string> SupportedTools { get; } =
            new HashSet<string>(["read_file"], StringComparer.Ordinal);

        public async ValueTask<ToolExecutionResult> ExecuteAsync(
            ProtocolResponse.ToolRequest request,
            CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("The blocked tool should have been cancelled.");
        }
    }

    private sealed class RecordingToolCallSink : IAgentToolCallSink
    {
        public List<AgentToolCallEvent> Events { get; } = [];

        public ValueTask WriteAsync(
            AgentToolCallEvent toolCall,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Events.Add(toolCall);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FailingBrain(BrainErrorCode errorCode) : IBrain
    {
        public string Provider => "failing";

        public ValueTask<BrainStatus> GetStatusAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new BrainStatus(Provider, BrainConnectionState.Connected));

        public ValueTask<BrainStatus> ConnectAsync(
            BrainConnectionRequest request,
            CancellationToken cancellationToken = default) =>
            GetStatusAsync(cancellationToken);

        public ValueTask DisconnectAsync(CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask<BrainResponse> SendAsync(
            BrainRequest request,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException<BrainResponse>(new BrainException(
                errorCode,
                "private diagnostic must not reach the UI"));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
