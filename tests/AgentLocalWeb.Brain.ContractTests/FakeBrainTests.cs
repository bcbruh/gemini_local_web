using AgentLocalWeb.Brain.Fake;

namespace AgentLocalWeb.Brain.ContractTests;

public sealed class FakeBrainTests
{
    [Fact]
    public async Task StartsDisconnectedAndConnectsExplicitly()
    {
        await using var brain = new FakeBrain();
        var testCancellation = TestContext.Current.CancellationToken;

        var before = await brain.GetStatusAsync(testCancellation);
        var after = await brain.ConnectAsync(
            new BrainConnectionRequest(Interactive: false),
            testCancellation);

        Assert.Equal(BrainConnectionState.Disconnected, before.State);
        Assert.Equal(BrainConnectionState.Connected, after.State);
        Assert.True(after.IsConnected);
    }

    [Fact]
    public async Task RefusesRequestsWhileDisconnected()
    {
        await using var brain = new FakeBrain([
            new BrainResponse.Final("response-1", "unused")
        ]);
        var testCancellation = TestContext.Current.CancellationToken;

        var exception = await Assert.ThrowsAsync<BrainException>(
            async () => await brain.SendAsync(Request("hello"), testCancellation));

        Assert.Equal(BrainErrorCode.AuthenticationFailed, exception.Code);
        Assert.False(exception.IsRetryable);
    }

    [Fact]
    public async Task ReturnsScriptedResponsesInOrderAndRecordsRequests()
    {
        await using var brain = new FakeBrain([
            new BrainResponse.Final("response-1", "first"),
            new BrainResponse.Final("response-2", "second")
        ]);
        var testCancellation = TestContext.Current.CancellationToken;
        await brain.ConnectAsync(
            new BrainConnectionRequest(Interactive: false),
            testCancellation);

        var first = await brain.SendAsync(Request("one"), testCancellation);
        var second = await brain.SendAsync(Request("two"), testCancellation);

        Assert.Equal("first", Assert.IsType<BrainResponse.Final>(first).Message);
        Assert.Equal("second", Assert.IsType<BrainResponse.Final>(second).Message);
        Assert.Equal(["one", "two"], brain.ReceivedRequests.Select(x => x.Messages.Single().Content));
    }

    [Fact]
    public async Task CancellationDoesNotConsumeScriptedResponse()
    {
        await using var brain = new FakeBrain(
            [new BrainResponse.Final("response-1", "still available")],
            TimeSpan.FromSeconds(5));
        var testCancellation = TestContext.Current.CancellationToken;
        await brain.ConnectAsync(
            new BrainConnectionRequest(Interactive: false),
            testCancellation);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(testCancellation);
        cancellation.CancelAfter(TimeSpan.FromMilliseconds(20));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await brain.SendAsync(Request("cancel"), cancellation.Token));

        Assert.Empty(brain.ReceivedRequests);
    }

    [Fact]
    public async Task ResponseFactorySupportsRepeatableOfflineConversation()
    {
        await using var brain = new FakeBrain(
            responseFactory: request => new BrainResponse.Final(
                "generated",
                $"echo:{request.Messages.Last().Content}"));
        var testCancellation = TestContext.Current.CancellationToken;
        await brain.ConnectAsync(
            new BrainConnectionRequest(Interactive: false),
            testCancellation);

        var first = await brain.SendAsync(Request("one"), testCancellation);
        var second = await brain.SendAsync(Request("two"), testCancellation);

        Assert.Equal("echo:one", Assert.IsType<BrainResponse.Final>(first).Message);
        Assert.Equal("echo:two", Assert.IsType<BrainResponse.Final>(second).Message);
    }

    private static BrainRequest Request(string content) => new(
        "conversation-1",
        [new BrainMessage(BrainRole.User, content)],
        "local-agent/v1");
}
