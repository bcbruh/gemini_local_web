namespace AgentLocalWeb.Brain.GeminiWeb;

internal enum GeminiWebSessionStatus
{
    Disconnected,
    Connected,
    ReconnectRequired
}

internal enum GeminiWebFailureKind
{
    Authentication,
    Compatibility,
    Network,
    Unavailable
}

internal sealed class GeminiWebSessionException : Exception
{
    public GeminiWebSessionException(
        GeminiWebFailureKind kind,
        string message,
        bool isRetryable = false,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Kind = kind;
        IsRetryable = isRetryable;
    }

    public GeminiWebFailureKind Kind { get; }

    public bool IsRetryable { get; }
}

internal interface IGeminiWebSession : IAsyncDisposable
{
    ValueTask<GeminiWebSessionStatus> ConnectAsync(
        bool interactive,
        CancellationToken cancellationToken);

    ValueTask<GeminiWebSessionStatus> GetStatusAsync(CancellationToken cancellationToken);

    ValueTask<string> SendPromptAsync(string prompt, CancellationToken cancellationToken);

    ValueTask DisconnectAsync(CancellationToken cancellationToken);
}
