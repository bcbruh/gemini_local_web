using System.Text;
using AgentLocalWeb.Persistence;
using Microsoft.AspNetCore.Http;

namespace AgentLocalWeb.AppHost.Tests;

public sealed class SseEventStreamTests
{
    [Fact]
    public async Task ReplaysEventsAfterCursorWithSseId()
    {
        using var directory = new TemporaryDirectory();
        await using var store = new SqliteAppStore(directory.File("state.db"));
        var testCancellation = TestContext.Current.CancellationToken;
        await store.InitializeAsync(testCancellation);
        await store.AppendEventAsync(
            "test.created",
            """{"value":42}""",
            cancellationToken: testCancellation);

        using var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            testCancellation);
        requestCancellation.CancelAfter(TimeSpan.FromSeconds(2));
        var body = new MemoryStream();
        var context = new DefaultHttpContext
        {
            RequestAborted = requestCancellation.Token
        };
        context.Request.QueryString = new QueryString("?after=0");
        context.Response.Body = body;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => SseEventStream.WriteAsync(context, store));

        var content = Encoding.UTF8.GetString(body.ToArray());
        Assert.Contains("id: 1\n", content, StringComparison.Ordinal);
        Assert.Contains("\"type\":\"test.created\"", content, StringComparison.Ordinal);
        Assert.Contains("\"value\":42", content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RejectsMalformedCursorBeforeOpeningStream()
    {
        using var directory = new TemporaryDirectory();
        await using var store = new SqliteAppStore(directory.File("state.db"));
        var cancellationToken = TestContext.Current.CancellationToken;
        await store.InitializeAsync(cancellationToken);
        var context = new DefaultHttpContext
        {
            RequestAborted = cancellationToken
        };
        context.Request.QueryString = new QueryString("?after=-1");
        context.Response.Body = new MemoryStream();

        await SseEventStream.WriteAsync(context, store);

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
    }

    [Fact]
    public async Task LastEventIdTakesPriorityWhenBrowserReconnects()
    {
        using var directory = new TemporaryDirectory();
        await using var store = new SqliteAppStore(directory.File("state.db"));
        var testCancellation = TestContext.Current.CancellationToken;
        await store.InitializeAsync(testCancellation);
        await store.AppendEventAsync(
            "test.first",
            "{}",
            cancellationToken: testCancellation);
        await store.AppendEventAsync(
            "test.second",
            "{}",
            cancellationToken: testCancellation);

        using var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            testCancellation);
        requestCancellation.CancelAfter(TimeSpan.FromSeconds(2));
        var body = new MemoryStream();
        var context = new DefaultHttpContext
        {
            RequestAborted = requestCancellation.Token
        };
        context.Request.QueryString = new QueryString("?after=0");
        context.Request.Headers["Last-Event-ID"] = "1";
        context.Response.Body = body;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => SseEventStream.WriteAsync(context, store));

        var content = Encoding.UTF8.GetString(body.ToArray());
        Assert.DoesNotContain("id: 1\n", content, StringComparison.Ordinal);
        Assert.Contains("id: 2\n", content, StringComparison.Ordinal);
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
