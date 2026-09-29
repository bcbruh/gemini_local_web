using System.Text.Json;

namespace AgentLocalWeb.Brain;

public abstract record BrainResponse(string RequestId)
{
    public sealed record Final(string RequestId, string Message)
        : BrainResponse(RequestId);

    public sealed record ToolRequest(
        string RequestId,
        string ProtocolVersion,
        string Tool,
        JsonElement Arguments)
        : BrainResponse(RequestId);
}
