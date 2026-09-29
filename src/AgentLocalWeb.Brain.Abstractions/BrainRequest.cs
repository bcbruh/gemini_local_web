using System.Collections.ObjectModel;

namespace AgentLocalWeb.Brain;

public sealed record BrainRequest
{
    public BrainRequest(
        string conversationId,
        IEnumerable<BrainMessage> messages,
        string protocolVersion,
        IReadOnlyDictionary<string, string>? metadata = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentException.ThrowIfNullOrWhiteSpace(protocolVersion);

        ConversationId = conversationId;
        Messages = new ReadOnlyCollection<BrainMessage>(messages.ToArray());
        ProtocolVersion = protocolVersion;
        Metadata = metadata is null
            ? new ReadOnlyDictionary<string, string>(new Dictionary<string, string>())
            : new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(metadata));
    }

    public string ConversationId { get; }

    public IReadOnlyList<BrainMessage> Messages { get; }

    public string ProtocolVersion { get; }

    public IReadOnlyDictionary<string, string> Metadata { get; }
}
