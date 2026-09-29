using System.Text;
using AgentLocalWeb.Brain;

namespace AgentLocalWeb.Agent.Core;

internal static class AgentContextBuilder
{
    private const int MessageEnvelopeBytes = 64;

    public static IReadOnlyList<BrainMessage> Build(
        string systemPrompt,
        IReadOnlyList<BrainMessage>? history,
        string userMessage,
        IReadOnlyList<BrainMessage> runMessages,
        int maximumBytes,
        int maximumHistoryMessages)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(systemPrompt);
        ArgumentException.ThrowIfNullOrWhiteSpace(userMessage);
        ArgumentNullException.ThrowIfNull(runMessages);

        var system = new BrainMessage(BrainRole.System, systemPrompt);
        var currentUser = new BrainMessage(BrainRole.User, userMessage);
        var requiredBytes = Measure(system) + Measure(currentUser);
        foreach (var message in runMessages)
        {
            requiredBytes = checked(requiredBytes + Measure(message));
        }

        if (requiredBytes > maximumBytes)
        {
            throw new AgentContextLimitException(
                "The current turn exceeds the context budget. Use a shorter request or narrower tool result.");
        }

        var selectedHistory = new List<BrainMessage>();
        var remainingBytes = maximumBytes - requiredBytes;
        if (history is not null && maximumHistoryMessages > 0)
        {
            foreach (var message in history
                         .Where(IsPersistableConversationRole)
                         .TakeLast(maximumHistoryMessages)
                         .Reverse())
            {
                var messageBytes = Measure(message);
                if (messageBytes > remainingBytes)
                {
                    break;
                }

                selectedHistory.Add(message);
                remainingBytes -= messageBytes;
            }

            selectedHistory.Reverse();
        }

        var result = new List<BrainMessage>(
            2 + selectedHistory.Count + runMessages.Count)
        {
            system
        };
        result.AddRange(selectedHistory);
        result.Add(currentUser);
        result.AddRange(runMessages);
        return result;
    }

    private static bool IsPersistableConversationRole(BrainMessage message) =>
        message.Role is BrainRole.User or BrainRole.Assistant;

    private static int Measure(BrainMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(message.Content);
        return checked(
            Encoding.UTF8.GetByteCount(message.Content) +
            (message.ToolCallId is null ? 0 : Encoding.UTF8.GetByteCount(message.ToolCallId)) +
            MessageEnvelopeBytes);
    }
}

internal sealed class AgentContextLimitException(string message) : Exception(message);
