using System.Diagnostics;

namespace AgentLocalWeb.GeminiWeb.Spike;

internal static class IsolatedBrowserLauncher
{
    private static readonly Uri GeminiUri = new("https://gemini.google.com/");

    public static string GetProfileDirectory(InstalledBrowser browser)
    {
        ArgumentNullException.ThrowIfNull(browser);

        var localApplicationData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);
        var browserDirectory = browser.Name.Contains(
            "Chrome",
            StringComparison.OrdinalIgnoreCase)
            ? "Chrome"
            : "Edge";

        return Path.Combine(
            localApplicationData,
            "AgentLocalWeb",
            "GeminiWebSpike",
            browserDirectory,
            "BrowserProfile");
    }

    public static Process Launch(InstalledBrowser browser, bool enableRemoteDebugging = false)
    {
        ArgumentNullException.ThrowIfNull(browser);

        var profileDirectory = GetProfileDirectory(browser);
        Directory.CreateDirectory(profileDirectory);

        var startInfo = new ProcessStartInfo
        {
            FileName = browser.ExecutablePath,
            UseShellExecute = false
        };

        startInfo.ArgumentList.Add($"--user-data-dir={profileDirectory}");
        startInfo.ArgumentList.Add("--no-first-run");
        startInfo.ArgumentList.Add("--no-default-browser-check");
        startInfo.ArgumentList.Add("--new-window");

        if (enableRemoteDebugging)
        {
            startInfo.ArgumentList.Add("--remote-debugging-address=127.0.0.1");
            startInfo.ArgumentList.Add("--remote-debugging-port=0");
        }

        startInfo.ArgumentList.Add(GeminiUri.AbsoluteUri);

        return Process.Start(startInfo)
            ?? throw new InvalidOperationException("The browser process could not be started.");
    }
}
