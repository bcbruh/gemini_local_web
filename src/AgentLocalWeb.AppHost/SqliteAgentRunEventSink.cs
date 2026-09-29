using System.Text.Json;
using AgentLocalWeb.Agent.Core;
using AgentLocalWeb.Persistence;

namespace AgentLocalWeb.AppHost;

internal sealed class SqliteAgentRunEventSink(SqliteAppStore store)
    : IAgentRunEventSink, IAgentToolCallSink
{
    public async ValueTask WriteAsync(
        AgentRunTransition transition,
        CancellationToken cancellationToken = default)
    {
        await store.TransitionRunAsync(
            transition.RunId,
            ToStorageValue(transition.Previous),
            ToStorageValue(transition.Current),
            transition.ErrorCode is null ? null : ToErrorCategory(transition.ErrorCode.Value),
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask WriteAsync(
        AgentToolCallEvent toolCall,
        CancellationToken cancellationToken = default)
    {
        if (toolCall.Status == AgentToolCallStatus.Requested)
        {
            await store.CreateToolCallAsync(
                toolCall.RunId,
                toolCall.RequestId,
                toolCall.Tool,
                cancellationToken).ConfigureAwait(false);
            return;
        }

        await store.FinishToolCallAsync(
            toolCall.RunId,
            toolCall.RequestId,
            ToStorageValue(toolCall.Status),
            toolCall.ErrorCode is null ? null : ToStorageValue(toolCall.ErrorCode.Value),
            cancellationToken).ConfigureAwait(false);
    }

    private static string ToStorageValue<T>(T value) where T : struct, Enum =>
        JsonNamingPolicy.SnakeCaseLower.ConvertName(value.ToString());

    private static string ToErrorCategory(AgentRunErrorCode value) => value switch
    {
        AgentRunErrorCode.InvalidProtocol => "protocol.invalid",
        AgentRunErrorCode.ToolTurnLimit => "run.tool_turn_limit",
        AgentRunErrorCode.ContextLimit => "context.limit",
        AgentRunErrorCode.BrainAuthentication => "brain.authentication",
        AgentRunErrorCode.BrainSessionExpired => "brain.session_expired",
        AgentRunErrorCode.BrainRateLimited => "brain.rate_limited",
        AgentRunErrorCode.BrainNetwork => "brain.network",
        AgentRunErrorCode.BrainCompatibility => "brain.compatibility",
        AgentRunErrorCode.BrainInvalidResponse => "brain.invalid_response",
        AgentRunErrorCode.BrainUnavailable => "brain.unavailable",
        AgentRunErrorCode.ToolFailure => "tool.failed",
        AgentRunErrorCode.Timeout => "tool.timeout",
        AgentRunErrorCode.Cancelled => "run.cancelled",
        _ => "run.failed"
    };
}
