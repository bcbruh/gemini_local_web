using System.Net.WebSockets;
using System.Text.Json;

namespace AgentLocalWeb.Brain.GeminiWeb;

internal sealed class CdpClient : IAsyncDisposable
{
    private readonly ClientWebSocket _socket = new();
    private int _nextId;

    internal string? LastCommand { get; private set; }

    internal int? LastErrorCode { get; private set; }

    internal bool LastEvaluationException { get; private set; }

    public async Task ConnectAsync(Uri socketUri, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(socketUri);
        if (socketUri.Scheme != "ws" ||
            !socketUri.Host.Equals("127.0.0.1", StringComparison.Ordinal))
        {
            throw new GeminiWebSessionException(
                GeminiWebFailureKind.Compatibility,
                "CDP connections are restricted to loopback WebSocket endpoints.");
        }

        try
        {
            await _socket.ConnectAsync(socketUri, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is WebSocketException or HttpRequestException)
        {
            throw new GeminiWebSessionException(
                GeminiWebFailureKind.Network,
                "Could not connect to the isolated Chrome window.",
                isRetryable: true,
                exception);
        }
    }

    public async Task<JsonElement> SendCommandAsync(
        string method,
        object? parameters,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        LastCommand = method;
        LastErrorCode = null;
        LastEvaluationException = false;
        var id = Interlocked.Increment(ref _nextId);
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            id,
            method,
            @params = parameters
        });

        try
        {
            await _socket.SendAsync(
                payload,
                WebSocketMessageType.Text,
                endOfMessage: true,
                cancellationToken).ConfigureAwait(false);

            while (true)
            {
                using var document = await ReceiveDocumentAsync(cancellationToken).ConfigureAwait(false);
                var root = document.RootElement;
                if (!root.TryGetProperty("id", out var responseId) || responseId.GetInt32() != id)
                {
                    continue;
                }

                if (root.TryGetProperty("error", out var error))
                {
                    var code = error.TryGetProperty("code", out var codeElement)
                        ? codeElement.GetInt32()
                        : 0;
                    LastErrorCode = code;
                    throw new GeminiWebSessionException(
                        GeminiWebFailureKind.Compatibility,
                        $"Chrome rejected the required DevTools command '{method}' (code {code}).");
                }

                if (!root.TryGetProperty("result", out var result))
                {
                    throw new GeminiWebSessionException(
                        GeminiWebFailureKind.Compatibility,
                        $"Chrome returned an invalid result for '{method}'.");
                }

                LastEvaluationException = result.TryGetProperty("exceptionDetails", out _);
                return result.Clone();
            }
        }
        catch (GeminiWebSessionException)
        {
            throw;
        }
        catch (WebSocketException exception)
        {
            throw new GeminiWebSessionException(
                GeminiWebFailureKind.Network,
                "The isolated Chrome connection was interrupted.",
                isRetryable: true,
                exception);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_socket.State == WebSocketState.Open)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            try
            {
                await _socket.CloseAsync(
                    WebSocketCloseStatus.NormalClosure,
                    "Adapter disconnected",
                    timeout.Token).ConfigureAwait(false);
            }
            catch (Exception exception) when (
                exception is WebSocketException or OperationCanceledException)
            {
                // Best-effort transport cleanup. No credentials or profile data are removed.
            }
        }

        _socket.Dispose();
    }

    private async Task<JsonDocument> ReceiveDocumentAsync(CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        WebSocketReceiveResult result;

        do
        {
            result = await _socket.ReceiveAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                throw new GeminiWebSessionException(
                    GeminiWebFailureKind.Network,
                    "The isolated Chrome connection closed unexpectedly.",
                    isRetryable: true);
            }

            await buffer.WriteAsync(chunk.AsMemory(0, result.Count), cancellationToken)
                .ConfigureAwait(false);
        }
        while (!result.EndOfMessage);

        try
        {
            return JsonDocument.Parse(buffer.ToArray());
        }
        catch (JsonException exception)
        {
            throw new GeminiWebSessionException(
                GeminiWebFailureKind.Compatibility,
                "Chrome returned malformed DevTools data.",
                innerException: exception);
        }
    }
}
