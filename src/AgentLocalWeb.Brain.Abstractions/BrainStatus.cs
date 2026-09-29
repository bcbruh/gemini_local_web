namespace AgentLocalWeb.Brain;

public sealed record BrainStatus(
    string Provider,
    BrainConnectionState State,
    string? UserMessage = null)
{
    public bool IsConnected => State == BrainConnectionState.Connected;
}
