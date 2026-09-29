using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentLocalWeb.Agent.Core;
using AgentLocalWeb.Brain;
using AgentLocalWeb.Brain.Fake;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace AgentLocalWeb.AppHost.Tests;

public sealed class LocalApplicationHttpTests
{
    [Fact]
    public async Task BootstrapCsrfAndSseReconnectWorkAcrossRealHttp()
    {
        using var directory = new TemporaryDirectory();
        var testCancellation = TestContext.Current.CancellationToken;
        await using var app = await LocalApplication.CreateAsync(
            ["--no-browser"],
            new LocalApplicationOptions(directory.File("state.db")),
            testCancellation);

        await app.StartAsync(testCancellation);
        try
        {
            var server = app.Services.GetRequiredService<IServer>();
            var address = Assert.Single(
                server.Features.Get<IServerAddressesFeature>()!.Addresses);
            var baseUri = new Uri(address, UriKind.Absolute);
            var accessSession = app.Services.GetRequiredService<LocalAccessSession>();
            using var handler = new HttpClientHandler
            {
                CookieContainer = new CookieContainer(),
                UseCookies = true
            };
            using var client = new HttpClient(handler) { BaseAddress = baseUri };
            client.DefaultRequestHeaders.Add(
                "Origin",
                baseUri.GetLeftPart(UriPartial.Authority));

            using var bootstrap = await client.PostAsJsonAsync(
                "/api/session/bootstrap",
                new { token = accessSession.BootstrapToken },
                testCancellation);
            Assert.Equal(HttpStatusCode.OK, bootstrap.StatusCode);
            var bootstrapBody = await bootstrap.Content.ReadFromJsonAsync<JsonElement>(
                testCancellation);
            var csrfToken = bootstrapBody.GetProperty("csrfToken").GetString();
            Assert.False(string.IsNullOrWhiteSpace(csrfToken));
            var setCookie = Assert.Single(bootstrap.Headers.GetValues("Set-Cookie"));
            Assert.Contains("HttpOnly", setCookie, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("SameSite=Strict", setCookie, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Path=/", setCookie, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(accessSession.BootstrapToken, setCookie, StringComparison.Ordinal);
            Assert.Contains(
                LocalAccessSession.CookieName,
                handler.CookieContainer.GetCookies(baseUri).Cast<Cookie>().Select(cookie => cookie.Name));

            using var secondBootstrap = await client.PostAsJsonAsync(
                "/api/session/bootstrap",
                new { token = accessSession.BootstrapToken },
                testCancellation);
            Assert.Equal(HttpStatusCode.Unauthorized, secondBootstrap.StatusCode);

            using var state = await client.GetAsync("/api/state", testCancellation);
            Assert.Equal(HttpStatusCode.OK, state.StatusCode);
            var stateBody = await state.Content.ReadFromJsonAsync<JsonElement>(testCancellation);
            var initialSequence = stateBody.GetProperty("latestEventSequence").GetInt64();
            Assert.True(initialSequence > 0);

            using var spoofedHostRequest = new HttpRequestMessage(HttpMethod.Get, "/api/state");
            spoofedHostRequest.Headers.Host = "example.com";
            using var spoofedHost = await client.SendAsync(spoofedHostRequest, testCancellation);
            Assert.Equal(HttpStatusCode.Forbidden, spoofedHost.StatusCode);

            using var foreignOriginClient = new HttpClient { BaseAddress = baseUri };
            using var foreignOriginRequest = new HttpRequestMessage(HttpMethod.Get, "/api/state");
            foreignOriginRequest.Headers.Add("Origin", "https://example.com");
            using var foreignOrigin = await foreignOriginClient.SendAsync(
                foreignOriginRequest,
                testCancellation);
            Assert.Equal(HttpStatusCode.Forbidden, foreignOrigin.StatusCode);

            using var missingCsrf = await client.PostAsync(
                "/api/brain/connect",
                content: null,
                testCancellation);
            Assert.Equal(HttpStatusCode.Forbidden, missingCsrf.StatusCode);

            using var connect = new HttpRequestMessage(HttpMethod.Post, "/api/brain/connect");
            connect.Headers.Add(LocalAccessSession.CsrfHeaderName, csrfToken);
            using var connected = await client.SendAsync(connect, testCancellation);
            Assert.Equal(HttpStatusCode.OK, connected.StatusCode);

            var firstEvent = await ReadOneEventAsync(
                client,
                $"/api/events?after={initialSequence}",
                lastEventId: null,
                testCancellation);
            Assert.Equal(initialSequence + 1, firstEvent.Id);
            Assert.Equal("brain.connection_changed", firstEvent.Type);

            using var reconnectTrigger = new HttpRequestMessage(HttpMethod.Post, "/api/brain/connect");
            reconnectTrigger.Headers.Add(LocalAccessSession.CsrfHeaderName, csrfToken);
            using var reconnected = await client.SendAsync(reconnectTrigger, testCancellation);
            Assert.Equal(HttpStatusCode.OK, reconnected.StatusCode);

            var eventAfterReconnect = await ReadOneEventAsync(
                client,
                "/api/events?after=0",
                firstEvent.Id,
                testCancellation);
            Assert.Equal(firstEvent.Id + 1, eventAfterReconnect.Id);
            Assert.Equal("brain.connection_changed", eventAfterReconnect.Type);

            using var send = new HttpRequestMessage(
                HttpMethod.Post,
                "/api/conversation/messages")
            {
                Content = JsonContent.Create(new { content = "hello agent" })
            };
            send.Headers.Add(LocalAccessSession.CsrfHeaderName, csrfToken);
            using var sent = await client.SendAsync(send, testCancellation);
            Assert.Equal(HttpStatusCode.OK, sent.StatusCode);
            var conversation = await sent.Content.ReadFromJsonAsync<JsonElement>(testCancellation);
            var messages = conversation.GetProperty("messages");
            Assert.Equal(2, messages.GetArrayLength());
            Assert.Contains(
                "Fake Brain đã nhận",
                messages[1].GetProperty("content").GetString(),
                StringComparison.Ordinal);

            using var completedState = await client.GetAsync("/api/state", testCancellation);
            var completedStateBody = await completedState.Content.ReadFromJsonAsync<JsonElement>(
                testCancellation);
            Assert.Equal(
                "completed",
                completedStateBody.GetProperty("latestRun").GetProperty("status").GetString());
        }
        finally
        {
            await app.StopAsync(testCancellation);
        }
    }

    [Fact]
    public async Task AgentLoopReadsSelectedWorkspaceThroughToolBoundary()
    {
        using var directory = new TemporaryDirectory();
        await File.WriteAllTextAsync(
            directory.File("sample.txt"),
            "workspace evidence",
            TestContext.Current.CancellationToken);
        var brain = new FakeBrain([
            ToolRequest("read-1", "read_file", """{"path":"sample.txt"}"""),
            Final("I inspected the selected workspace.")
        ]);
        var testCancellation = TestContext.Current.CancellationToken;
        await using var app = await LocalApplication.CreateAsync(
            ["--no-browser"],
            new LocalApplicationOptions(
                directory.File("state.db"),
                Brain: brain,
                WorkspacePicker: new FixedWorkspacePicker(directory.Path)),
            testCancellation);

        await app.StartAsync(testCancellation);
        try
        {
            var (client, csrfToken) = await CreateAuthenticatedClientAsync(
                app,
                testCancellation);
            using (client)
            {
                using var connect = Post("/api/brain/connect", csrfToken);
                using var connected = await client.SendAsync(connect, testCancellation);
                Assert.Equal(HttpStatusCode.OK, connected.StatusCode);

                using var pick = Post("/api/workspace/pick", csrfToken);
                using var picked = await client.SendAsync(pick, testCancellation);
                Assert.Equal(HttpStatusCode.OK, picked.StatusCode);

                using var send = Post(
                    "/api/conversation/messages",
                    csrfToken,
                    JsonContent.Create(new { content = "Inspect sample.txt" }));
                using var sent = await client.SendAsync(send, testCancellation);
                Assert.Equal(HttpStatusCode.OK, sent.StatusCode);
                var conversation = await sent.Content.ReadFromJsonAsync<JsonElement>(
                    testCancellation);
                Assert.Equal(
                    "I inspected the selected workspace.",
                    conversation.GetProperty("messages")[1].GetProperty("content").GetString());

                var secondBrainRequest = Assert.Single(brain.ReceivedRequests.Skip(1));
                var observation = Assert.Single(
                    secondBrainRequest.Messages,
                    message => message.Role == BrainRole.Tool);
                Assert.Contains("workspace evidence", observation.Content, StringComparison.Ordinal);

                using var state = await client.GetAsync("/api/state", testCancellation);
                var stateBody = await state.Content.ReadFromJsonAsync<JsonElement>(testCancellation);
                Assert.Equal(
                    "completed",
                    stateBody.GetProperty("latestRun").GetProperty("status").GetString());
                var toolCall = Assert.Single(stateBody.GetProperty("latestToolCalls").EnumerateArray());
                Assert.Equal("read_file", toolCall.GetProperty("tool").GetString());
                Assert.Equal("succeeded", toolCall.GetProperty("status").GetString());
                Assert.False(toolCall.TryGetProperty("requestId", out _));
                Assert.False(toolCall.TryGetProperty("output", out _));
            }
        }
        finally
        {
            await app.StopAsync(testCancellation);
        }
    }

    [Fact]
    public async Task CancelEndpointStopsActiveRunAndPersistsCancellation()
    {
        using var directory = new TemporaryDirectory();
        var brain = new FakeBrain(
            [Final("too late")],
            responseDelay: TimeSpan.FromSeconds(30));
        var testCancellation = TestContext.Current.CancellationToken;
        await using var app = await LocalApplication.CreateAsync(
            ["--no-browser"],
            new LocalApplicationOptions(directory.File("state.db"), Brain: brain),
            testCancellation);

        await app.StartAsync(testCancellation);
        try
        {
            var (client, csrfToken) = await CreateAuthenticatedClientAsync(
                app,
                testCancellation);
            using (client)
            {
                using var connect = Post("/api/brain/connect", csrfToken);
                using var connected = await client.SendAsync(connect, testCancellation);
                Assert.Equal(HttpStatusCode.OK, connected.StatusCode);

                using var send = Post(
                    "/api/conversation/messages",
                    csrfToken,
                    JsonContent.Create(new { content = "long request" }));
                var sendTask = client.SendAsync(send, testCancellation);
                await WaitForActiveRunAsync(app, testCancellation);

                using var cancel = Post("/api/runs/cancel", csrfToken);
                using var cancelled = await client.SendAsync(cancel, testCancellation);
                Assert.Equal(HttpStatusCode.Accepted, cancelled.StatusCode);

                using var sendResponse = await sendTask;
                Assert.Equal(HttpStatusCode.Conflict, sendResponse.StatusCode);
                var failure = await sendResponse.Content.ReadFromJsonAsync<JsonElement>(
                    testCancellation);
                Assert.Equal("Cancelled", failure.GetProperty("code").GetString());

                using var state = await client.GetAsync("/api/state", testCancellation);
                var stateBody = await state.Content.ReadFromJsonAsync<JsonElement>(testCancellation);
                var run = stateBody.GetProperty("latestRun");
                Assert.Equal("cancelled", run.GetProperty("status").GetString());
                Assert.True(run.GetProperty("cancelRequested").GetBoolean());
            }
        }
        finally
        {
            await app.StopAsync(testCancellation);
        }
    }

    [Fact]
    public async Task PatchWaitsForHashBoundApprovalThenAppliesAndCompletes()
    {
        using var directory = new TemporaryDirectory();
        var filePath = directory.File("sample.txt");
        await File.WriteAllTextAsync(
            filePath,
            "before value\n",
            new UTF8Encoding(false),
            TestContext.Current.CancellationToken);
        var expectedHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(
            filePath,
            TestContext.Current.CancellationToken))).ToLowerInvariant();
        var brain = new FakeBrain(responseFactory: request =>
        {
            var observations = request.Messages.Where(message => message.Role == BrainRole.Tool)
                .ToArray();
            if (observations.Length == 0)
            {
                return ToolRequest(
                    "prepare-1",
                    "prepare_patch",
                    JsonSerializer.Serialize(new
                    {
                        files = new[]
                        {
                            new
                            {
                                path = "sample.txt",
                                expected_sha256 = expectedHash,
                                replacements = new[]
                                {
                                    new { old_text = "before value", new_text = "after value" }
                                }
                            }
                        }
                    }));
            }

            if (observations[^1].Content.Contains(
                    "\"tool\":\"prepare_patch\"",
                    StringComparison.Ordinal))
            {
                using var envelope = JsonDocument.Parse(observations[^1].Content);
                var output = envelope.RootElement.GetProperty("output");
                return ToolRequest(
                    "apply-1",
                    "apply_patch",
                    JsonSerializer.Serialize(new
                    {
                        action_hash = output.GetProperty("action_hash").GetString()
                    }));
            }

            return Final("The approved patch was applied.");
        });
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var app = await LocalApplication.CreateAsync(
            ["--no-browser"],
            new LocalApplicationOptions(
                directory.File("state.db"),
                Brain: brain,
                WorkspacePicker: new FixedWorkspacePicker(directory.Path)),
            cancellationToken);

        await app.StartAsync(cancellationToken);
        try
        {
            var (client, csrfToken) = await CreateAuthenticatedClientAsync(app, cancellationToken);
            using (client)
            {
                using var connected = await client.SendAsync(
                    Post("/api/brain/connect", csrfToken), cancellationToken);
                using var picked = await client.SendAsync(
                    Post("/api/workspace/pick", csrfToken), cancellationToken);
                Assert.Equal(HttpStatusCode.OK, connected.StatusCode);
                Assert.Equal(HttpStatusCode.OK, picked.StatusCode);

                using var send = Post(
                    "/api/conversation/messages",
                    csrfToken,
                    JsonContent.Create(new { content = "Change the sample" }));
                var sendTask = client.SendAsync(send, cancellationToken);
                var approval = await WaitForApprovalAsync(client, cancellationToken);
                Assert.Equal("waiting_for_approval", approval.State.GetProperty("latestRun")
                    .GetProperty("status").GetString());
                Assert.Contains("-before value", approval.Approval.GetProperty("diff").GetString(),
                    StringComparison.Ordinal);
                Assert.Equal("before value\n", await File.ReadAllTextAsync(filePath, cancellationToken));

                using var wrongDecision = Post(
                    $"/api/approvals/{approval.Approval.GetProperty("id").GetString()}/decision",
                    csrfToken,
                    JsonContent.Create(new
                    {
                        actionHash = new string('f', 64),
                        decision = "Approved"
                    }));
                using var wrong = await client.SendAsync(wrongDecision, cancellationToken);
                Assert.Equal(HttpStatusCode.Conflict, wrong.StatusCode);
                Assert.Equal("before value\n", await File.ReadAllTextAsync(filePath, cancellationToken));

                using var approve = Post(
                    $"/api/approvals/{approval.Approval.GetProperty("id").GetString()}/decision",
                    csrfToken,
                    JsonContent.Create(new
                    {
                        actionHash = approval.Approval.GetProperty("actionHash").GetString(),
                        decision = "Approved"
                    }));
                using var approved = await client.SendAsync(approve, cancellationToken);
                Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
                using var sent = await sendTask;
                Assert.Equal(HttpStatusCode.OK, sent.StatusCode);
                Assert.Equal("after value\n", await File.ReadAllTextAsync(filePath, cancellationToken));

                using var completed = await client.GetAsync("/api/state", cancellationToken);
                var completedState = await completed.Content.ReadFromJsonAsync<JsonElement>(
                    cancellationToken);
                Assert.Equal("completed", completedState.GetProperty("latestRun")
                    .GetProperty("status").GetString());
                Assert.Equal(JsonValueKind.Null, completedState.GetProperty("pendingApproval").ValueKind);
            }
        }
        finally
        {
            await app.StopAsync(cancellationToken);
        }
    }

    [Fact]
    public async Task TypedBrainFailureIsPersistedAndReturnedWithoutPrivateDiagnostic()
    {
        using var directory = new TemporaryDirectory();
        var testCancellation = TestContext.Current.CancellationToken;
        await using var app = await LocalApplication.CreateAsync(
            ["--no-browser"],
            new LocalApplicationOptions(
                directory.File("state.db"),
                Brain: new FailingBrain(BrainErrorCode.RateLimited)),
            testCancellation);

        await app.StartAsync(testCancellation);
        try
        {
            var (client, csrfToken) = await CreateAuthenticatedClientAsync(
                app,
                testCancellation);
            using (client)
            {
                using var connect = Post("/api/brain/connect", csrfToken);
                using var connected = await client.SendAsync(connect, testCancellation);
                Assert.Equal(HttpStatusCode.OK, connected.StatusCode);

                using var send = Post(
                    "/api/conversation/messages",
                    csrfToken,
                    JsonContent.Create(new { content = "question" }));
                using var failed = await client.SendAsync(send, testCancellation);
                Assert.Equal(HttpStatusCode.Conflict, failed.StatusCode);
                var failure = await failed.Content.ReadFromJsonAsync<JsonElement>(testCancellation);
                Assert.Equal("BrainRateLimited", failure.GetProperty("code").GetString());
                Assert.DoesNotContain(
                    "private diagnostic",
                    failure.GetProperty("error").GetString(),
                    StringComparison.Ordinal);

                using var state = await client.GetAsync("/api/state", testCancellation);
                var stateBody = await state.Content.ReadFromJsonAsync<JsonElement>(testCancellation);
                Assert.Equal(
                    "brain.rate_limited",
                    stateBody.GetProperty("latestRun").GetProperty("errorCategory").GetString());
            }
        }
        finally
        {
            await app.StopAsync(testCancellation);
        }
    }

    private static async Task WaitForActiveRunAsync(
        WebApplication app,
        CancellationToken cancellationToken)
    {
        var engine = app.Services.GetRequiredService<AgentRunEngine>();
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (engine.ActiveRunId is null)
        {
            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TimeoutException("The test run did not become active.");
            }

            await Task.Delay(20, cancellationToken);
        }
    }

    private static async Task<(HttpClient Client, string CsrfToken)> CreateAuthenticatedClientAsync(
        WebApplication app,
        CancellationToken cancellationToken)
    {
        var server = app.Services.GetRequiredService<IServer>();
        var address = Assert.Single(server.Features.Get<IServerAddressesFeature>()!.Addresses);
        var baseUri = new Uri(address, UriKind.Absolute);
        var handler = new HttpClientHandler
        {
            CookieContainer = new CookieContainer(),
            UseCookies = true
        };
        var client = new HttpClient(handler) { BaseAddress = baseUri };
        client.DefaultRequestHeaders.Add("Origin", baseUri.GetLeftPart(UriPartial.Authority));
        var accessSession = app.Services.GetRequiredService<LocalAccessSession>();
        using var bootstrap = await client.PostAsJsonAsync(
            "/api/session/bootstrap",
            new { token = accessSession.BootstrapToken },
            cancellationToken);
        bootstrap.EnsureSuccessStatusCode();
        var body = await bootstrap.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        return (client, body.GetProperty("csrfToken").GetString()!);
    }

    private static HttpRequestMessage Post(
        string path,
        string csrfToken,
        HttpContent? content = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = content };
        request.Headers.Add(LocalAccessSession.CsrfHeaderName, csrfToken);
        return request;
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

    private static async Task<(JsonElement State, JsonElement Approval)> WaitForApprovalAsync(
        HttpClient client,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (true)
        {
            using var response = await client.GetAsync("/api/state", cancellationToken);
            var state = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
            var approval = state.GetProperty("pendingApproval");
            if (approval.ValueKind == JsonValueKind.Object)
            {
                return (state, approval);
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TimeoutException("The patch approval did not become pending.");
            }

            await Task.Delay(20, cancellationToken);
        }
    }

    private static BrainResponse.Final Final(string message) => new(
        "final-1",
        JsonSerializer.Serialize(new
        {
            protocol = ProtocolV1.Version,
            type = "final",
            message
        }));

    private static async Task<SseEvent> ReadOneEventAsync(
        HttpClient client,
        string requestUri,
        long? lastEventId,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        if (lastEventId is not null)
        {
            request.Headers.Add(
                "Last-Event-ID",
                lastEventId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        using var response = await client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            timeout.Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);

        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        using var reader = new StreamReader(stream);
        string? id = null;
        string? data = null;
        while (true)
        {
            var line = await reader.ReadLineAsync(timeout.Token);
            Assert.NotNull(line);
            if (line.Length == 0)
            {
                break;
            }

            if (line.StartsWith("id: ", StringComparison.Ordinal))
            {
                id = line[4..];
            }
            else if (line.StartsWith("data: ", StringComparison.Ordinal))
            {
                data = line[6..];
            }
        }

        Assert.NotNull(id);
        Assert.NotNull(data);
        using var envelope = JsonDocument.Parse(data);
        return new SseEvent(
            long.Parse(id, System.Globalization.CultureInfo.InvariantCulture),
            envelope.RootElement.GetProperty("type").GetString()!);
    }

    private sealed record SseEvent(long Id, string Type);

    private sealed class FixedWorkspacePicker(string path) : IWorkspacePicker
    {
        public Task<string?> PickAsync(
            string? initialDirectory,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(path);
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
                "private diagnostic must not reach the local API"));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "AgentLocalWeb.HttpTests",
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
