namespace AgentLocalWeb.Brain.GeminiWeb;

internal static class ChromeDiscovery
{
    public static string? FindExecutable()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        return CandidatePaths()
            .Where(File.Exists)
            .Select(Path.GetFullPath)
            .FirstOrDefault();
    }

    private static IEnumerable<string> CandidatePaths()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        yield return Path.Combine(programFiles, "Google", "Chrome", "Application", "chrome.exe");
        yield return Path.Combine(programFilesX86, "Google", "Chrome", "Application", "chrome.exe");
        yield return Path.Combine(localData, "Google", "Chrome", "Application", "chrome.exe");
    }
}
