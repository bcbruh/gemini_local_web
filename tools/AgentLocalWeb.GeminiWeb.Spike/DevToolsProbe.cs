using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace AgentLocalWeb.GeminiWeb.Spike;

internal static class DevToolsProbe
{
    public static async Task<DevToolsProbeResult> ProbeAsync(
        InstalledBrowser browser,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(browser);

        var (port, targets) = await GetTargetsAsync(browser, cancellationToken)
            .ConfigureAwait(false);

        var pageTargets = targets.Where(target => target.Type == "page").ToArray();
        var hasGeminiPage = pageTargets.Any(target =>
            Uri.TryCreate(target.Url, UriKind.Absolute, out var uri) &&
            uri.Scheme == Uri.UriSchemeHttps &&
            uri.Host.Equals("gemini.google.com", StringComparison.OrdinalIgnoreCase));

        return new DevToolsProbeResult(port, pageTargets.Length, hasGeminiPage);
    }

    public static async Task<Uri> GetGeminiPageSocketAsync(
        InstalledBrowser browser,
        CancellationToken cancellationToken)
    {
        var (_, targets) = await GetTargetsAsync(browser, cancellationToken)
            .ConfigureAwait(false);
        var target = targets.FirstOrDefault(candidate =>
            candidate.Type == "page" &&
            Uri.TryCreate(candidate.Url, UriKind.Absolute, out var uri) &&
            uri.Scheme == Uri.UriSchemeHttps &&
            uri.Host.Equals("gemini.google.com", StringComparison.OrdinalIgnoreCase));

        if (target is null ||
            !Uri.TryCreate(target.WebSocketDebuggerUrl, UriKind.Absolute, out var socketUri) ||
            socketUri.Scheme != "ws")
        {
            throw new InvalidOperationException("No debuggable Gemini page was found.");
        }

        return socketUri;
    }

    private static async Task<(int Port, DevToolsTarget[] Targets)> GetTargetsAsync(
        InstalledBrowser browser,
        CancellationToken cancellationToken)
    {
        var profileDirectory = IsolatedBrowserLauncher.GetProfileDirectory(browser);
        var activePortPath = Path.Combine(profileDirectory, "DevToolsActivePort");
        if (!File.Exists(activePortPath))
        {
            throw new InvalidOperationException(
                "DevToolsActivePort was not found. Close the isolated browser and relaunch it with 'launch-debug'.");
        }

        var lines = await File.ReadAllLinesAsync(activePortPath, cancellationToken)
            .ConfigureAwait(false);
        if (lines.Length < 2 ||
            !int.TryParse(lines[0], out var port) ||
            port is < 1 or > 65535 ||
            !lines[1].StartsWith("/devtools/browser/", StringComparison.Ordinal))
        {
            throw new InvalidDataException("DevToolsActivePort has an invalid format.");
        }

        using var client = new HttpClient
        {
            BaseAddress = new Uri($"http://127.0.0.1:{port}"),
            Timeout = TimeSpan.FromSeconds(3)
        };

        var targets = await client.GetFromJsonAsync<DevToolsTarget[]>(
            "/json/list",
            cancellationToken).ConfigureAwait(false) ?? [];
        return (port, targets);
    }

    private sealed record DevToolsTarget(
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("url")] string Url,
        [property: JsonPropertyName("webSocketDebuggerUrl")] string? WebSocketDebuggerUrl);
}

internal sealed record DevToolsProbeResult(
    int Port,
    int PageCount,
    bool HasGeminiPage);
