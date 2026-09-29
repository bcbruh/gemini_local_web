using System.Text.Json;

namespace AgentLocalWeb.GeminiWeb.Spike;

internal static class GeminiRoundTripProbe
{
    public static async Task RunAsync(
        InstalledBrowser browser,
        CancellationToken cancellationToken)
    {
        var socketUri = await DevToolsProbe.GetGeminiPageSocketAsync(
            browser,
            cancellationToken).ConfigureAwait(false);
        await using var client = new CdpClient();
        await client.ConnectAsync(socketUri, cancellationToken).ConfigureAwait(false);
        await client.SendCommandAsync(
            "Accessibility.enable",
            parameters: null,
            cancellationToken).ConfigureAwait(false);

        var tree = await client.SendCommandAsync(
            "Accessibility.getFullAXTree",
            parameters: null,
            cancellationToken).ConfigureAwait(false);
        var textboxNodeId = FindPromptTextboxBackendNodeId(tree);

        await client.SendCommandAsync(
            "DOM.focus",
            new { backendNodeId = textboxNodeId },
            cancellationToken).ConfigureAwait(false);

        var nonce = $"LOCAL_AGENT_SPIKE_{Guid.NewGuid():N}".ToUpperInvariant();
        var prompt = $"Reply with exactly this token and nothing else: {nonce}";
        await client.SendCommandAsync(
            "Input.insertText",
            new { text = prompt },
            cancellationToken).ConfigureAwait(false);
        await client.SendCommandAsync(
            "Input.dispatchKeyEvent",
            new
            {
                type = "keyDown",
                key = "Enter",
                code = "Enter",
                windowsVirtualKeyCode = 13,
                nativeVirtualKeyCode = 13
            },
            cancellationToken).ConfigureAwait(false);
        await client.SendCommandAsync(
            "Input.dispatchKeyEvent",
            new
            {
                type = "keyUp",
                key = "Enter",
                code = "Enter",
                windowsVirtualKeyCode = 13,
                nativeVirtualKeyCode = 13
            },
            cancellationToken).ConfigureAwait(false);

        using var responseTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        responseTimeout.CancelAfter(TimeSpan.FromSeconds(45));
        while (true)
        {
            responseTimeout.Token.ThrowIfCancellationRequested();
            if (await HasAtLeastTwoOccurrencesAsync(client, nonce, responseTimeout.Token)
                .ConfigureAwait(false))
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), responseTimeout.Token)
                .ConfigureAwait(false);
        }
    }

    private static int FindPromptTextboxBackendNodeId(JsonElement tree)
    {
        foreach (var node in tree.GetProperty("nodes").EnumerateArray())
        {
            if (node.TryGetProperty("ignored", out var ignored) && ignored.GetBoolean())
            {
                continue;
            }

            if (!node.TryGetProperty("role", out var role) ||
                !role.TryGetProperty("value", out var roleValue) ||
                roleValue.GetString() != "textbox" ||
                !node.TryGetProperty("backendDOMNodeId", out var backendNodeId))
            {
                continue;
            }

            return backendNodeId.GetInt32();
        }

        throw new InvalidOperationException(
            "No accessible prompt textbox was found. Confirm Gemini is signed in and the chat page is visible.");
    }

    private static async Task<bool> HasAtLeastTwoOccurrencesAsync(
        CdpClient client,
        string nonce,
        CancellationToken cancellationToken)
    {
        var encodedNonce = JsonSerializer.Serialize(nonce);
        var expression = $$"""
            (() => {
              const text = document.body?.innerText ?? "";
              const token = {{encodedNonce}};
              return text.split(token).length - 1 >= 2;
            })()
            """;
        var evaluation = await client.SendCommandAsync(
            "Runtime.evaluate",
            new { expression, returnByValue = true },
            cancellationToken).ConfigureAwait(false);

        return evaluation
            .GetProperty("result")
            .GetProperty("value")
            .GetBoolean();
    }
}
