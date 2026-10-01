using System.Diagnostics;
using AgentLocalWeb.AppHost;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

var app = await LocalApplication.CreateAsync(args).ConfigureAwait(false);
await app.StartAsync().ConfigureAwait(false);

var address = app.Services.GetRequiredService<IServer>()
    .Features.Get<IServerAddressesFeature>()?
    .Addresses.SingleOrDefault()
    ?? throw new InvalidOperationException("The local server address was not published.");
var accessSession = app.Services.GetRequiredService<LocalAccessSession>();
var startUri = $"{address}/#bootstrap={Uri.EscapeDataString(accessSession.BootstrapToken)}";

if (!args.Contains("--no-browser", StringComparer.OrdinalIgnoreCase))
{
    try
    {
        var chromeRequested = args.Contains("--browser=chrome", StringComparer.OrdinalIgnoreCase);
        var chromePath = chromeRequested ? FindChromeExecutable() : null;
        if (chromePath is null)
        {
            _ = Process.Start(new ProcessStartInfo(startUri) { UseShellExecute = true });
        }
        else
        {
            var startInfo = new ProcessStartInfo(chromePath) { UseShellExecute = false };
            startInfo.ArgumentList.Add(startUri);
            _ = Process.Start(startInfo);
        }
    }
    catch (Exception exception) when (
        exception is InvalidOperationException or System.ComponentModel.Win32Exception)
    {
        app.Logger.LogWarning("Could not open the default browser. Navigate to the local app manually.");
    }
}

app.Logger.LogInformation("AgentLocalWeb is listening on {Address}", address);
await app.WaitForShutdownAsync().ConfigureAwait(false);

static string? FindChromeExecutable()
{
    var candidates = new[]
    {
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "Google", "Chrome", "Application", "chrome.exe"),
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "Google", "Chrome", "Application", "chrome.exe"),
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Google", "Chrome", "Application", "chrome.exe")
    };
    return candidates.FirstOrDefault(File.Exists);
}
