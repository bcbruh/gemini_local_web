using System.Diagnostics;

namespace AgentLocalWeb.GeminiWeb.Spike;

internal static class BrowserDiscovery
{
    public static IReadOnlyList<InstalledBrowser> FindSupportedBrowsers()
    {
        if (!OperatingSystem.IsWindows())
        {
            return [];
        }

        return CandidatePaths()
            .Where(candidate => File.Exists(candidate.Path))
            .DistinctBy(candidate => candidate.Path, StringComparer.OrdinalIgnoreCase)
            .Select(candidate => new InstalledBrowser(
                candidate.Name,
                Path.GetFullPath(candidate.Path),
                FileVersionInfo.GetVersionInfo(candidate.Path).ProductVersion))
            .ToArray();
    }

    private static IEnumerable<(string Name, string Path)> CandidatePaths()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var localApplicationData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);

        yield return ("Microsoft Edge", Path.Combine(
            programFilesX86,
            "Microsoft",
            "Edge",
            "Application",
            "msedge.exe"));
        yield return ("Microsoft Edge", Path.Combine(
            programFiles,
            "Microsoft",
            "Edge",
            "Application",
            "msedge.exe"));
        yield return ("Google Chrome", Path.Combine(
            programFiles,
            "Google",
            "Chrome",
            "Application",
            "chrome.exe"));
        yield return ("Google Chrome", Path.Combine(
            programFilesX86,
            "Google",
            "Chrome",
            "Application",
            "chrome.exe"));
        yield return ("Google Chrome", Path.Combine(
            localApplicationData,
            "Google",
            "Chrome",
            "Application",
            "chrome.exe"));
    }
}
