namespace AgentLocalWeb.Brain;

public interface IBrain : IAsyncDisposable
{
    string Provider { get; }

    ValueTask<BrainStatus> GetStatusAsync(CancellationToken cancellationToken = default);

    ValueTask<BrainStatus> ConnectAsync(
        BrainConnectionRequest request,
        CancellationToken cancellationToken = default);

    ValueTask DisconnectAsync(CancellationToken cancellationToken = default);

    ValueTask<BrainResponse> SendAsync(
        BrainRequest request,
        CancellationToken cancellationToken = default);
}
