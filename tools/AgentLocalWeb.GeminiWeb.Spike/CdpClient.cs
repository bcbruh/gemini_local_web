using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace AgentLocalWeb.GeminiWeb.Spike;

internal sealed class CdpClient : IAsyncDisposable
{
    private readonly ClientWebSocket _socket = new();
    private int _nextId;

    public async Task ConnectAsync(Uri socketUri, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(socketUri);
        if (socketUri.Scheme != "ws" || !socketUri.Host.Equals("127.0.0.1", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("CDP connections are restricted to loopback WebSocket endpoints.");
        }

        await _socket.ConnectAsync(socketUri, cancellationToken).ConfigureAwait(false);
    }

    public async Task<JsonElement> SendCommandAsync(
        string method,
        object? parameters,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        var id = Interlocked.Increment(ref _nextId);
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            id,
            method,
            @params = parameters
        });

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
                throw new InvalidOperationException(
                    $"CDP command '{method}' failed with code {error.GetProperty("code").GetInt32()}.");
            }

            return root.GetProperty("result").Clone();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_socket.State == WebSocketState.Open)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            await _socket.CloseAsync(
                WebSocketCloseStatus.NormalClosure,
                "Spike completed",
                timeout.Token).ConfigureAwait(false);
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
                throw new InvalidOperationException("The CDP WebSocket closed unexpectedly.");
            }

            await buffer.WriteAsync(chunk.AsMemory(0, result.Count), cancellationToken)
                .ConfigureAwait(false);
        }
        while (!result.EndOfMessage);

        return JsonDocument.Parse(buffer.ToArray());
    }
}
