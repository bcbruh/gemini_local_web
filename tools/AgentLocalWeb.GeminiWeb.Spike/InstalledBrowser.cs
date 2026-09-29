namespace AgentLocalWeb.GeminiWeb.Spike;

internal sealed record InstalledBrowser(
    string Name,
    string ExecutablePath,
    string? Version);
