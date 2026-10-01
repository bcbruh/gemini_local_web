using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text.Json;
using AgentLocalWeb.Brain.GeminiWeb;

namespace AgentLocalWeb.Brain.ContractTests;

public sealed class GeminiPageDiagnosticsTests
{
    [Theory]
    [InlineData(GeminiPageStage.Composer)]
    [InlineData(GeminiPageStage.Typing)]
    [InlineData(GeminiPageStage.VerifyText)]
    [InlineData(GeminiPageStage.PressEnterSubmit)]
    [InlineData(GeminiPageStage.FindSend)]
    [InlineData(GeminiPageStage.ClickSubmit)]
    [InlineData(GeminiPageStage.ConfirmSubmission)]
    [InlineData(GeminiPageStage.ReadResponse)]
    internal async Task ReportsExactFailingStageWithoutCdpPayload(GeminiPageStage failingStage)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var ct = timeout.Token;
        // Loopback fixture only. No Chrome, Gemini account or external network access.
        using var portReservation = new TcpListener(IPAddress.Loopback, 0);
        portReservation.Start();
        var port = ((IPEndPoint)portReservation.LocalEndpoint).Port;
        portReservation.Stop();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        var server = ServeAsync(listener, failingStage, ct);
        await using var client = new CdpClient();
        await client.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/"), ct);
        GeminiPageFailureDiagnostic? diagnostic = null;
        var page = new GeminiPageClient(client,
            new GeminiWebOptions { PollInterval = TimeSpan.FromMilliseconds(1) },
            failure => diagnostic = failure);

        await Assert.ThrowsAsync<GeminiWebSessionException>(() =>
            page.SendPromptAsync("PRIVATE_PROMPT_CANARY", ct));
        await client.DisposeAsync();
        await server;

        Assert.NotNull(diagnostic);
        Assert.Equal(failingStage, diagnostic.Stage);
        Assert.Equal(-32000, diagnostic.CdpErrorCode);
        Assert.DoesNotContain("PRIVATE", diagnostic.ToLogLine());
        Assert.Contains($"stage={failingStage}", diagnostic.ToLogLine());
    }

    [Fact]
    public void DiagnosticOutputAllowsOnlyKnownMethodNames()
    {
        var diagnostic = new GeminiPageFailureDiagnostic(
            GeminiPageStage.ReadResponse, "PRIVATE_PAYLOAD", -32000, true);
        Assert.Equal(
            "gemini.failure stage=ReadResponse cdp=other code=-32000 evaluation_exception=True",
            diagnostic.ToLogLine());
    }

    private static async Task ServeAsync(
        HttpListener listener,
        GeminiPageStage failingStage,
        CancellationToken cancellationToken)
    {
        var context = await listener.GetContextAsync().WaitAsync(cancellationToken);
        var accepted = await context.AcceptWebSocketAsync(null);
        using var socket = accepted.WebSocket;
        var chunk = new byte[16384];
        var submitted = false;
        while (true)
        {
            using var payload = new MemoryStream();
            WebSocketReceiveResult received;
            do
            {
                received = await socket.ReceiveAsync(chunk, cancellationToken);
                if (received.MessageType == WebSocketMessageType.Close) return;
                payload.Write(chunk, 0, received.Count);
            } while (!received.EndOfMessage);

            using var document = JsonDocument.Parse(payload.ToArray());
            var root = document.RootElement;
            var method = root.GetProperty("method").GetString();
            var parameters = root.GetProperty("params");
            var expression = parameters.ValueKind == JsonValueKind.Object &&
                parameters.TryGetProperty("expression", out var script) ? script.GetString()! : "";
            var stage = method switch
            {
                "Input.insertText" => GeminiPageStage.Typing,
                "DOM.getBoxModel" => GeminiPageStage.ClickSubmit,
                "Input.dispatchKeyEvent" when
                    parameters.TryGetProperty("key", out var key) &&
                    key.GetString() == "Enter" => GeminiPageStage.PressEnterSubmit,
                "Runtime.evaluate" when expression.Contains("right.score - left.score", StringComparison.Ordinal) &&
                    !expression.Contains("const rawControls", StringComparison.Ordinal) => GeminiPageStage.Composer,
                "Runtime.evaluate" when expression.Contains("normalizedExpected", StringComparison.Ordinal) => GeminiPageStage.VerifyText,
                "Runtime.evaluate" when expression.Contains("const rawControls", StringComparison.Ordinal) => GeminiPageStage.FindSend,
                "Runtime.evaluate" when expression.Contains("hasNewResponse", StringComparison.Ordinal) => GeminiPageStage.ConfirmSubmission,
                "Runtime.evaluate" when expression.Contains("const selectors", StringComparison.Ordinal) => GeminiPageStage.ReadResponse,
                _ => GeminiPageStage.Baseline
            };
            if (stage == failingStage)
            {
                var error = JsonSerializer.SerializeToUtf8Bytes(new
                {
                    id = root.GetProperty("id").GetInt32(),
                    error = new { code = -32000, message = "PRIVATE_RESPONSE_COOKIE_HEADER_TOKEN_HTML_CANARY" }
                });
                await socket.SendAsync(error, WebSocketMessageType.Text, true, cancellationToken);
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Fixture complete", cancellationToken);
                return;
            }

            object result = new { };
            if (method == "DOM.describeNode") result = new { node = new { backendNodeId = 1 } };
            if (method == "DOM.getBoxModel") result = new { model = new { border = new[] { 0, 0, 20, 0, 20, 20, 0, 20 } } };
            if (method == "Input.dispatchMouseEvent") submitted = true;
            if (stage == GeminiPageStage.PressEnterSubmit &&
                failingStage == GeminiPageStage.ReadResponse)
            {
                submitted = true;
            }
            if (method == "Runtime.evaluate")
            {
                result = stage switch
                {
                    GeminiPageStage.Composer or GeminiPageStage.FindSend => new { result = new { objectId = "fixture" } },
                    GeminiPageStage.VerifyText => new { result = new { value = true } },
                    GeminiPageStage.ConfirmSubmission => new { result = new { value = submitted } },
                    _ when expression.Contains("stopSelector", StringComparison.Ordinal) => new { result = new { value = false } },
                    _ => new { result = new { value = 0 } }
                };
            }

            var response = JsonSerializer.SerializeToUtf8Bytes(new { id = root.GetProperty("id").GetInt32(), result });
            await socket.SendAsync(response, WebSocketMessageType.Text, true, cancellationToken);
        }
    }
}
