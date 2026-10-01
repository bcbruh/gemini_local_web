using AgentLocalWeb.Agent.Core;
using AgentLocalWeb.Brain;
using AgentLocalWeb.Persistence;

namespace AgentLocalWeb.AppHost;

internal sealed class ConversationService(
    SqliteAppStore store,
    IBrain brain,
    AgentRunEngine runEngine) : IDisposable
{
    private readonly SemaphoreSlim _turnLock = new(1, 1);

    public Task<string> InitializeAsync(CancellationToken cancellationToken = default) =>
        store.GetOrCreateActiveConversationAsync(cancellationToken);

    public Task<PersistedConversation?> GetCurrentAsync(
        CancellationToken cancellationToken = default) =>
        store.GetActiveConversationAsync(cancellationToken);

    public async Task<PersistedConversation> StartNewAsync(
        CancellationToken cancellationToken = default)
    {
        if (runEngine.ActiveRunId is not null)
        {
            throw new InvalidOperationException("Cannot start a new session while a run is active.");
        }

        await _turnLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (runEngine.ActiveRunId is not null)
            {
                throw new InvalidOperationException("Cannot start a new session while a run is active.");
            }

            await store.StartNewActiveConversationAsync(cancellationToken).ConfigureAwait(false);
            return await store.GetActiveConversationAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The new conversation is unavailable.");
        }
        finally
        {
            _turnLock.Release();
        }
    }

    public async Task<PersistedConversation> SendAsync(
        string content,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(content);
        await _turnLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var status = await brain.GetStatusAsync(cancellationToken).ConfigureAwait(false);
            if (!status.IsConnected)
            {
                throw new BrainException(
                    BrainErrorCode.AuthenticationFailed,
                    "Connect the brain before sending a message.");
            }

            var conversationId = await store.GetOrCreateActiveConversationAsync(cancellationToken)
                .ConfigureAwait(false);
            var before = await store.GetActiveConversationAsync(cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException("The active conversation is unavailable.");
            await store.AppendConversationMessageAsync(
                conversationId,
                "user",
                content,
                cancellationToken).ConfigureAwait(false);

            var runId = Guid.NewGuid().ToString("N");
            await store.CreateRunAsync(
                runId,
                conversationId,
                ProtocolV1.Version,
                AgentRunEngine.PromptVersion,
                cancellationToken).ConfigureAwait(false);
            var result = await runEngine.RunAsync(
                new AgentRunRequest(
                    conversationId,
                    content,
                    runId,
                    before.Messages.Select(ToBrainMessage).ToArray()),
                cancellationToken).ConfigureAwait(false);
            if (result.State != RunState.Completed || result.FinalMessage is null)
            {
                throw new AgentRunFailedException(result);
            }

            await store.AppendConversationMessageAsync(
                conversationId,
                "assistant",
                result.FinalMessage,
                cancellationToken).ConfigureAwait(false);
            return await store.GetActiveConversationAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The active conversation is unavailable.");
        }
        finally
        {
            _turnLock.Release();
        }
    }

    public void Dispose() => _turnLock.Dispose();

    private static BrainMessage ToBrainMessage(PersistedMessage message) => new(
        message.Role switch
        {
            "system" => BrainRole.System,
            "user" => BrainRole.User,
            "assistant" => BrainRole.Assistant,
            "tool" => BrainRole.Tool,
            _ => throw new InvalidOperationException("Stored message role is invalid.")
        },
        message.Content);
}

internal sealed class AgentRunFailedException(AgentRunResult result)
    : Exception(result.Error?.Message ?? "The agent run did not complete.")
{
    public AgentRunResult Result { get; } = result;
}
