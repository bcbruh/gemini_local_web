namespace AgentLocalWeb.Brain;

public sealed record BrainConnectionRequest(
    bool Interactive,
    bool ForceReconnect = false);
