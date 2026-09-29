using AgentLocalWeb.GeminiWeb.Spike;
using AgentLocalWeb.Agent.Core;
using AgentLocalWeb.Brain;
using AgentLocalWeb.Brain.GeminiWeb;
using AgentLocalWeb.Tools;
using AgentLocalWeb.Workspace;

const string Usage = """
Gemini Web feasibility spike

Commands:
  discover      List supported installed browsers without opening them.
  launch-login [edge|chrome]
                Open Gemini in a visible isolated browser profile for manual sign-in.
  launch-debug [edge|chrome]
                Open the isolated profile with loopback DevTools enabled.
  probe-tabs [edge|chrome]
                Confirm DevTools and a Gemini page are reachable without reading page data.
  roundtrip [edge|chrome]
                Send a harmless nonce prompt and verify its response without logging page content.
  adapter-roundtrip chrome
                Verify the production brain adapter with a harmless nonce prompt.
  agent-readonly-roundtrip chrome
                Verify Gemini requests a local read tool and uses its observation.

This utility does not read cookies or inspect an existing browser profile.
""";

var command = args.FirstOrDefault()?.ToLowerInvariant() ?? "discover";
var browsers = BrowserDiscovery.FindSupportedBrowsers();

if (command == "discover")
{
    if (browsers.Count == 0)
    {
        Console.Error.WriteLine("No supported Edge or Chrome installation was found.");
        return 2;
    }

    foreach (var browser in browsers)
    {
        Console.WriteLine($"{browser.Name} {browser.Version ?? "unknown-version"}");
        Console.WriteLine($"  {browser.ExecutablePath}");
    }

    return 0;
}

if (command is "launch-login" or "launch-debug" or "probe-tabs" or "roundtrip" or
    "adapter-roundtrip" or "agent-readonly-roundtrip")
{
    var requestedBrowser = args.ElementAtOrDefault(1)?.ToLowerInvariant();
    var browser = requestedBrowser switch
    {
        "edge" => browsers.FirstOrDefault(candidate =>
            candidate.Name.Contains("Edge", StringComparison.OrdinalIgnoreCase)),
        "chrome" => browsers.FirstOrDefault(candidate =>
            candidate.Name.Contains("Chrome", StringComparison.OrdinalIgnoreCase)),
        null => browsers.FirstOrDefault(),
        _ => null
    };

    if (browser is null)
    {
        Console.Error.WriteLine(requestedBrowser is null
            ? "No supported Edge or Chrome installation was found."
            : $"The requested browser '{requestedBrowser}' was not found.");
        return 2;
    }

    if (command == "probe-tabs")
    {
        var result = await DevToolsProbe.ProbeAsync(browser, CancellationToken.None);
        Console.WriteLine($"DevTools loopback endpoint is reachable on port {result.Port}.");
        Console.WriteLine($"Page targets: {result.PageCount}");
        Console.WriteLine($"Gemini page reachable: {result.HasGeminiPage}");
        return result.HasGeminiPage ? 0 : 4;
    }

    if (command == "roundtrip")
    {
        Console.WriteLine("Sending a non-sensitive nonce prompt through the visible Gemini page.");
        await GeminiRoundTripProbe.RunAsync(browser, CancellationToken.None);
        Console.WriteLine("Round-trip passed: Gemini returned the expected nonce.");
        return 0;
    }

    if (command == "adapter-roundtrip")
    {
        if (!browser.Name.Contains("Chrome", StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine("The production adapter currently supports Google Chrome only.");
            return 2;
        }

        Console.WriteLine("Sending a non-sensitive nonce through the production GeminiWebBrain adapter.");
        var nonce = $"LOCAL_AGENT_ADAPTER_{Guid.NewGuid():N}".ToUpperInvariant();
        await using var brain = new GeminiWebBrain(new GeminiWebOptions
        {
            ChromeExecutablePath = browser.ExecutablePath,
            ProfileDirectory = IsolatedBrowserLauncher.GetProfileDirectory(browser),
            ResponseTimeout = TimeSpan.FromMinutes(3)
        });
        var status = await brain.ConnectAsync(new BrainConnectionRequest(Interactive: true));
        if (!status.IsConnected)
        {
            Console.Error.WriteLine("Gemini needs manual sign-in in the visible Chrome window.");
            return 5;
        }

        var response = await brain.SendAsync(new BrainRequest(
            "adapter-smoke",
            [new BrainMessage(BrainRole.User, $"Reply with exactly this token and nothing else: {nonce}")],
            "local-agent/v1"));
        if (response is not BrainResponse.Final final ||
            !final.Message.Contains(nonce, StringComparison.Ordinal))
        {
            Console.Error.WriteLine("The production adapter returned an unexpected response.");
            return 6;
        }

        Console.WriteLine("Production adapter round-trip passed.");
        return 0;
    }

    if (command == "agent-readonly-roundtrip")
    {
        if (!browser.Name.Contains("Chrome", StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine("The production adapter currently supports Google Chrome only.");
            return 2;
        }

        Console.WriteLine("Running a read-only agent loop through the production Gemini adapter.");
        var workspacePath = Path.Combine(
            Path.GetTempPath(),
            "AgentLocalWeb.GeminiReadOnlySmoke",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspacePath);
        var nonce = $"LOCAL_FILE_{Guid.NewGuid():N}".ToUpperInvariant();
        await File.WriteAllTextAsync(Path.Combine(workspacePath, "proof.txt"), nonce);
        try
        {
            await using var brain = new GeminiWebBrain(new GeminiWebOptions
            {
                ChromeExecutablePath = browser.ExecutablePath,
                ProfileDirectory = IsolatedBrowserLauncher.GetProfileDirectory(browser),
                ResponseTimeout = TimeSpan.FromMinutes(3)
            });
            var status = await brain.ConnectAsync(new BrainConnectionRequest(Interactive: true));
            if (!status.IsConnected)
            {
                Console.Error.WriteLine("Gemini needs manual sign-in in the visible Chrome window.");
                return 5;
            }

            var selection = WorkspaceManager.Validate(workspacePath);
            var tools = new ReadOnlyWorkspaceTools(new WorkspaceReader(selection));
            using var engine = new AgentRunEngine(
                brain,
                tools,
                new AgentRunOptions(Duration: TimeSpan.FromMinutes(8)));
            var result = await engine.RunAsync(new AgentRunRequest(
                "live-readonly-smoke",
                "Read proof.txt with the read_file tool. After observing it, return a final envelope " +
                "whose message contains the exact file token."));
            if (result.State != RunState.Completed ||
                result.FinalMessage is null ||
                !result.FinalMessage.Contains(nonce, StringComparison.Ordinal))
            {
                Console.Error.WriteLine(
                    $"Read-only agent loop failed safely (state={result.State}, " +
                    $"error={result.Error?.Code}, detail={result.Error?.Message}).");
                return 7;
            }

            Console.WriteLine("Read-only agent round-trip passed.");
            return 0;
        }
        finally
        {
            Directory.Delete(workspacePath, recursive: true);
        }
    }

    var enableRemoteDebugging = command == "launch-debug";
    Console.WriteLine($"Opening {browser.Name} with an isolated profile.");
    Console.WriteLine($"Profile: {IsolatedBrowserLauncher.GetProfileDirectory(browser)}");
    Console.WriteLine("Complete sign-in manually in the visible browser. Do not share credentials with this utility.");
    _ = IsolatedBrowserLauncher.Launch(browser, enableRemoteDebugging);
    return 0;
}

Console.Error.WriteLine(Usage);
return 1;
