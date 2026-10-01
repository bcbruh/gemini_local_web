namespace AgentLocalWeb.Agent.Core;

public sealed record AgentRunRequest(
    string ConversationId,
    string UserMessage,
    string? RunId = null,
    IReadOnlyList<AgentLocalWeb.Brain.BrainMessage>? History = null);

public sealed record AgentRunOptions(
    int MaximumToolTurns = 8,
    int MaximumProtocolRetries = 0,
    TimeSpan? Duration = null,
    int MaximumObservationBytes = 128 * 1024,
    int MaximumContextBytes = 512 * 1024,
    int MaximumHistoryMessages = 40)
{
    public TimeSpan MaximumDuration { get; } = Duration ?? TimeSpan.FromMinutes(8);
}

public sealed record AgentRunResult(
    string RunId,
    RunState State,
    string? FinalMessage,
    AgentRunError? Error);

public sealed record AgentRunError(
    AgentRunErrorCode Code,
    string Message);

public enum AgentRunErrorCode
{
    InvalidProtocol,
    ToolTurnLimit,
    ContextLimit,
    BrainAuthentication,
    BrainSessionExpired,
    BrainRateLimited,
    BrainNetwork,
    BrainCompatibility,
    BrainInvalidResponse,
    BrainUnavailable,
    ToolFailure,
    Timeout,
    Cancelled
}

public sealed record AgentRunTransition(
    string RunId,
    RunState Previous,
    RunState Current,
    AgentRunErrorCode? ErrorCode = null);

public interface IAgentRunEventSink
{
    ValueTask WriteAsync(
        AgentRunTransition transition,
        CancellationToken cancellationToken = default);
}

public enum AgentToolCallStatus
{
    Requested,
    Succeeded,
    Failed,
    Cancelled
}

public sealed record AgentToolCallEvent(
    string RunId,
    string RequestId,
    string Tool,
    AgentToolCallStatus Status,
    ToolExecutionErrorCode? ErrorCode = null);

public interface IAgentToolCallSink
{
    ValueTask WriteAsync(
        AgentToolCallEvent toolCall,
        CancellationToken cancellationToken = default);
}

internal sealed class NullAgentToolCallSink : IAgentToolCallSink
{
    public static NullAgentToolCallSink Instance { get; } = new();

    public ValueTask WriteAsync(
        AgentToolCallEvent toolCall,
        CancellationToken cancellationToken = default) =>
        ValueTask.CompletedTask;
}

internal sealed class NullAgentRunEventSink : IAgentRunEventSink
{
    public static NullAgentRunEventSink Instance { get; } = new();

    public ValueTask WriteAsync(
        AgentRunTransition transition,
        CancellationToken cancellationToken = default) =>
        ValueTask.CompletedTask;
}
