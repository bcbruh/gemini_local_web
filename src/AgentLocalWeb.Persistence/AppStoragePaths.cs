namespace AgentLocalWeb.Persistence;

public static class AppStoragePaths
{
    public static string GetDefaultDataDirectory()
    {
        var localApplicationData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localApplicationData))
        {
            throw new InvalidOperationException("The local application data directory is unavailable.");
        }

        return Path.Combine(localApplicationData, "AgentLocalWeb", "Data");
    }

    public static string GetDefaultDatabasePath() =>
        Path.Combine(GetDefaultDataDirectory(), "agent-local-web.db");
}
