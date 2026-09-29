using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace AgentLocalWeb.Brain.GeminiWeb;

internal static class ChromeDevToolsEndpoint
{
    public static async Task<Uri?> TryFindGeminiPageAsync(
        string profileDirectory,
        TimeSpan requestTimeout,
        CancellationToken cancellationToken)
    {
        var activePortPath = Path.Combine(profileDirectory, "DevToolsActivePort");
        if (!File.Exists(activePortPath))
        {
            return null;
        }

        string[] lines;
        try
        {
            lines = await File.ReadAllLinesAsync(activePortPath, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (IOException)
        {
            return null;
        }

        if (lines.Length < 2 ||
            !int.TryParse(lines[0], out var port) ||
            port is < 1 or > 65535 ||
            !lines[1].StartsWith("/devtools/browser/", StringComparison.Ordinal))
        {
            throw new GeminiWebSessionException(
                GeminiWebFailureKind.Compatibility,
                "Chrome published an invalid DevTools endpoint.");
        }

        using var client = new HttpClient
        {
            BaseAddress = new Uri($"http://127.0.0.1:{port}"),
            Timeout = requestTimeout
        };

        DevToolsTarget[] targets;
        try
        {
            targets = await client.GetFromJsonAsync<DevToolsTarget[]>("/json/list", cancellationToken)
                .ConfigureAwait(false) ?? [];
        }
        catch (Exception exception) when (
            exception is HttpRequestException or TaskCanceledException &&
            !cancellationToken.IsCancellationRequested)
        {
            return null;
        }

        var target = targets.FirstOrDefault(candidate =>
            candidate.Type == "page" &&
            IsGeminiUrl(candidate.Url));
        if (target is null)
        {
            return null;
        }

        if (!Uri.TryCreate(target.WebSocketDebuggerUrl, UriKind.Absolute, out var socketUri) ||
            socketUri.Scheme != "ws" ||
            !socketUri.Host.Equals("127.0.0.1", StringComparison.Ordinal))
        {
            throw new GeminiWebSessionException(
                GeminiWebFailureKind.Compatibility,
                "Chrome did not publish a safe loopback page endpoint.");
        }

        return socketUri;
    }

    private static bool IsGeminiUrl(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps &&
        uri.Host.Equals("gemini.google.com", StringComparison.OrdinalIgnoreCase);

    private sealed record DevToolsTarget(
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("url")] string Url,
        [property: JsonPropertyName("webSocketDebuggerUrl")] string? WebSocketDebuggerUrl);
}
