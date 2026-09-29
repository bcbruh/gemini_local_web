namespace AgentLocalWeb.Brain;

public sealed record BrainMessage(
    BrainRole Role,
    string Content,
    string? ToolCallId = null);
