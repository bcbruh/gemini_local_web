using System.Collections.Concurrent;

namespace AgentLocalWeb.Brain.Fake;

public sealed class FakeBrain : IBrain
{
    private readonly ConcurrentQueue<BrainResponse> _responses;
    private readonly ConcurrentQueue<BrainRequest> _requests = new();
    private readonly TimeSpan _responseDelay;
    private readonly Func<BrainRequest, BrainResponse>? _responseFactory;
    private BrainConnectionState _state = BrainConnectionState.Disconnected;
    private bool _disposed;

    public FakeBrain(
        IEnumerable<BrainResponse>? responses = null,
        TimeSpan? responseDelay = null,
        Func<BrainRequest, BrainResponse>? responseFactory = null)
    {
        _responses = new ConcurrentQueue<BrainResponse>(responses ?? []);
        _responseDelay = responseDelay ?? TimeSpan.Zero;
        _responseFactory = responseFactory;
    }

    public string Provider => "fake";

    public IReadOnlyCollection<BrainRequest> ReceivedRequests => _requests.ToArray();

    public void Enqueue(BrainResponse response)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(response);
        _responses.Enqueue(response);
    }

    public ValueTask<BrainStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(CurrentStatus());
    }

    public ValueTask<BrainStatus> ConnectAsync(
        BrainConnectionRequest request,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        _state = BrainConnectionState.Connected;
        return ValueTask.FromResult(CurrentStatus());
    }

    public ValueTask DisconnectAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        _state = BrainConnectionState.Disconnected;
        return ValueTask.CompletedTask;
    }

    public async ValueTask<BrainResponse> SendAsync(
        BrainRequest request,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        if (_state != BrainConnectionState.Connected)
        {
            throw new BrainException(
                BrainErrorCode.AuthenticationFailed,
                "Fake brain is not connected.");
        }

        if (_responseDelay > TimeSpan.Zero)
        {
            await Task.Delay(_responseDelay, cancellationToken).ConfigureAwait(false);
        }

        if (!_responses.TryDequeue(out var response) && _responseFactory is null)
        {
            throw new BrainException(
                BrainErrorCode.Unavailable,
                "Fake brain has no scripted response remaining.");
        }

        response ??= _responseFactory!(request);

        _requests.Enqueue(request);
        return response;
    }

    public ValueTask DisposeAsync()
    {
        _disposed = true;
        _state = BrainConnectionState.Disconnected;
        return ValueTask.CompletedTask;
    }

    private BrainStatus CurrentStatus() => new(Provider, _state);
}
