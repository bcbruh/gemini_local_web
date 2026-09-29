using System.Text.Json;

namespace AgentLocalWeb.Brain.GeminiWeb;

internal sealed class GeminiPageClient(CdpClient client, GeminiWebOptions options)
{
    private const int StablePollsRequired = 4;

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await client.SendCommandAsync("Accessibility.enable", null, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<bool> HasPromptAsync(CancellationToken cancellationToken)
    {
        return await FindPromptBackendNodeIdAsync(cancellationToken).ConfigureAwait(false) is not null;
    }

    public async Task<string> SendPromptAsync(
        string prompt,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);

        var textboxNodeId = await FindPromptBackendNodeIdAsync(cancellationToken)
            .ConfigureAwait(false);
        if (textboxNodeId is null)
        {
            throw new GeminiWebSessionException(
                GeminiWebFailureKind.Authentication,
                "The Gemini prompt is unavailable. Sign in again and reconnect.");
        }

        await WaitForPageIdleAsync(cancellationToken).ConfigureAwait(false);
        var baselineMarker = Guid.NewGuid().ToString("N");
        await MarkExistingResponsesAsync(baselineMarker, cancellationToken).ConfigureAwait(false);
        await client.SendCommandAsync("Page.bringToFront", null, cancellationToken)
            .ConfigureAwait(false);
        await client.SendCommandAsync(
            "DOM.focus",
            new { backendNodeId = textboxNodeId.Value },
            cancellationToken).ConfigureAwait(false);
        await SelectPromptTextAsync(cancellationToken).ConfigureAwait(false);
        await SetPromptTextAsync(prompt, cancellationToken).ConfigureAwait(false);
        if (!await PromptMatchesAsync(prompt, cancellationToken).ConfigureAwait(false))
        {
            throw new GeminiWebSessionException(
                GeminiWebFailureKind.Compatibility,
                "The Gemini composer did not contain the requested prompt after typing.");
        }

        if (!await ClickSendButtonAsync(baselineMarker, cancellationToken).ConfigureAwait(false))
        {
            throw new GeminiWebSessionException(
                GeminiWebFailureKind.Compatibility,
                "Could not locate an enabled Gemini Send button near the composer.");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.ResponseTimeout);

        string? lastText = null;
        var stablePolls = 0;
        try
        {
            while (true)
            {
                var current = await ReadNewResponsesAsync(baselineMarker, timeout.Token)
                    .ConfigureAwait(false);
                var response = current.LastOrDefault();
                var generationInProgress = await IsGenerationInProgressAsync(timeout.Token)
                    .ConfigureAwait(false);
                if (!generationInProgress && !string.IsNullOrWhiteSpace(response))
                {
                    if (string.Equals(response, lastText, StringComparison.Ordinal))
                    {
                        stablePolls++;
                    }
                    else
                    {
                        lastText = response;
                        stablePolls = 1;
                    }

                    if (stablePolls >= StablePollsRequired)
                    {
                        return response;
                    }
                }

                await Task.Delay(options.PollInterval, timeout.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            var diagnostics = await ReadDiagnosticsAsync(baselineMarker, cancellationToken)
                .ConfigureAwait(false);
            await TryStopGenerationAsync(cancellationToken).ConfigureAwait(false);
            throw new GeminiWebSessionException(
                GeminiWebFailureKind.Network,
                $"Timed out while waiting for Gemini to finish its response " +
                $"(response_nodes={diagnostics.ResponseNodes}, " +
                $"new_response_nodes={diagnostics.NewResponseNodes}, " +
                $"composer_chars={diagnostics.ComposerCharacters}, " +
                $"send_buttons={diagnostics.SendButtons}, " +
                $"near_buttons={diagnostics.NearButtons}, " +
                $"enabled_near_buttons={diagnostics.EnabledNearButtons}, " +
                $"near_button_metadata={diagnostics.NearButtonMetadata}, " +
                $"stop_controls={diagnostics.StopControls}, " +
                $"busy_elements={diagnostics.BusyElements}).",
                isRetryable: true,
                exception);
        }
        catch (OperationCanceledException)
        {
            await TryStopGenerationAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private async Task WaitForPageIdleAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.ResponseTimeout);
        var previousResponseCount = -1;
        var stablePolls = 0;
        try
        {
            while (stablePolls < StablePollsRequired)
            {
                var responseCount = await ReadResponseNodeCountAsync(timeout.Token)
                    .ConfigureAwait(false);
                var generating = await IsGenerationInProgressAsync(timeout.Token)
                    .ConfigureAwait(false);
                if (!generating && responseCount == previousResponseCount)
                {
                    stablePolls++;
                }
                else
                {
                    stablePolls = 0;
                    previousResponseCount = responseCount;
                }

                await Task.Delay(options.PollInterval, timeout.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            await TryStopGenerationAsync(cancellationToken).ConfigureAwait(false);
            throw new GeminiWebSessionException(
                GeminiWebFailureKind.Network,
                "A previous Gemini response did not become idle before the next request.",
                isRetryable: true,
                exception);
        }
    }

    private async Task<int> ReadResponseNodeCountAsync(CancellationToken cancellationToken)
    {
        const string expression = """
            document.querySelectorAll('model-response, [data-test-id="model-response"], .model-response-text, .response-container-content').length
            """;
        var evaluation = await client.SendCommandAsync(
            "Runtime.evaluate",
            new { expression, returnByValue = true },
            cancellationToken).ConfigureAwait(false);
        return evaluation.GetProperty("result").GetProperty("value").GetInt32();
    }

    private async Task SelectPromptTextAsync(CancellationToken cancellationToken)
    {
        await client.SendCommandAsync(
            "Input.dispatchKeyEvent",
            new
            {
                type = "keyDown",
                key = "a",
                code = "KeyA",
                modifiers = 2,
                windowsVirtualKeyCode = 65,
                nativeVirtualKeyCode = 65
            },
            cancellationToken).ConfigureAwait(false);
        await client.SendCommandAsync(
            "Input.dispatchKeyEvent",
            new
            {
                type = "keyUp",
                key = "a",
                code = "KeyA",
                modifiers = 2,
                windowsVirtualKeyCode = 65,
                nativeVirtualKeyCode = 65
            },
            cancellationToken).ConfigureAwait(false);
    }

    private async Task SetPromptTextAsync(
        string prompt,
        CancellationToken cancellationToken)
    {
        await client.SendCommandAsync(
            "Input.insertText",
            new { text = prompt },
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> PromptMatchesAsync(
        string prompt,
        CancellationToken cancellationToken)
    {
        var expected = JsonSerializer.Serialize(prompt.Replace("\r\n", "\n", StringComparison.Ordinal));
        var expression = $$"""
            (() => {
              const selector = [
                'rich-textarea [contenteditable="true"]',
                '.ql-editor[contenteditable="true"]',
                '[contenteditable="true"][role="textbox"]',
                '[contenteditable="plaintext-only"][role="textbox"]',
                'textarea[role="textbox"]',
                'textarea[aria-label*="prompt" i]',
                'textarea[placeholder*="Gemini" i]'
              ].join(',');
              const candidates = Array.from(document.querySelectorAll(selector)).filter(element => {
                const rectangle = element.getBoundingClientRect();
                const style = getComputedStyle(element);
                return rectangle.width > 0 && rectangle.height > 0 &&
                  style.visibility !== 'hidden' && style.display !== 'none';
              });
              const expected = {{expected}};
              const normalize = text => text
                .replace(/\r\n?/g, '\n')
                .replace(/\u00a0/g, ' ')
                .replace(/[\u200b\ufeff]/g, '')
                .normalize('NFC');
              const normalizedExpected = normalize(expected);
              return candidates.some(candidate =>
                ('value' in candidate
                  ? [candidate.value]
                  : [candidate.innerText, candidate.textContent])
                  .some(text => {
                    if (typeof text !== 'string') return false;
                    const actual = normalize(text);
                    return actual === normalizedExpected || actual === `${normalizedExpected}\n`;
                  }));
            })()
            """;
        var evaluation = await client.SendCommandAsync(
            "Runtime.evaluate",
            new { expression, returnByValue = true },
            cancellationToken).ConfigureAwait(false);
        return evaluation.TryGetProperty("result", out var result) &&
            result.TryGetProperty("value", out var value) &&
            value.ValueKind == JsonValueKind.True;
    }

    private async Task<bool> ClickSendButtonAsync(
        string baselineMarker,
        CancellationToken cancellationToken)
    {
        var marker = JsonSerializer.Serialize(baselineMarker);
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var expression = $$"""
                (() => {
                  const attemptMarker = {{marker}};
                  const promptSelector = [
                    'rich-textarea [contenteditable="true"]',
                    '.ql-editor[contenteditable="true"]',
                    '[contenteditable="true"][role="textbox"]',
                    '[contenteditable="plaintext-only"][role="textbox"]',
                    'textarea[role="textbox"]',
                    'textarea[aria-label*="prompt" i]',
                    'textarea[placeholder*="Gemini" i]'
                  ].join(',');
                  const promptValue = element => ('value' in element
                    ? element.value
                    : element.innerText || element.textContent || '');
                  const prompts = Array.from(document.querySelectorAll(promptSelector)).filter(element => {
                    const rectangle = element.getBoundingClientRect();
                    const style = getComputedStyle(element);
                    return rectangle.width > 0 && rectangle.height > 0 &&
                      style.visibility !== 'hidden' && style.display !== 'none' &&
                      promptValue(element).length > 0;
                  });
                  const prompt = prompts.sort((left, right) =>
                    promptValue(right).length - promptValue(left).length ||
                    right.getBoundingClientRect().bottom - left.getBoundingClientRect().bottom)[0];
                  if (!prompt) return null;
                  prompt.setAttribute('data-agent-local-web-composer', attemptMarker);
                  const promptRectangle = prompt.getBoundingClientRect();
                  const rawControls = Array.from(document.querySelectorAll([
                    'button',
                    '[role="button"]',
                    '[data-test-id*="send" i]',
                    '[data-testid*="send" i]'
                  ].join(',')));
                  const controls = Array.from(new Set(rawControls.map(element =>
                    element.closest('button, [role="button"]') || element)));
                  const buttons = controls
                    .filter(element =>
                      element.getAttribute('data-agent-local-web-send-attempt') !== attemptMarker)
                    .map(element => {
                      const rectangle = element.getBoundingClientRect();
                      const horizontalDistance = Math.max(
                        promptRectangle.left - rectangle.right,
                        rectangle.left - promptRectangle.right,
                        0);
                      const verticalDistance = Math.max(
                        promptRectangle.top - rectangle.bottom,
                        rectangle.top - promptRectangle.bottom,
                        0);
                      const x = rectangle.left + rectangle.width / 2;
                      const y = rectangle.top + rectangle.height / 2;
                      const accessibleLabel = [
                        element.getAttribute('aria-label'),
                        element.getAttribute('title'),
                        element.getAttribute('mattooltip'),
                        element.getAttribute('data-tooltip'),
                        element.getAttribute('data-tooltip-text')
                      ].filter(value => typeof value === 'string').join(' ').toLocaleLowerCase();
                      const testId = [
                        element.getAttribute('data-test-id'),
                        element.getAttribute('data-testid'),
                        element.querySelector('[data-test-id], [data-testid]')?.getAttribute('data-test-id'),
                        element.querySelector('[data-test-id], [data-testid]')?.getAttribute('data-testid')
                      ].filter(value => typeof value === 'string').join(' ').toLocaleLowerCase();
                      const className = typeof element.className === 'string'
                        ? element.className.toLocaleLowerCase() : '';
                      const iconMetadata = Array.from(element.querySelectorAll(
                        'mat-icon, [class*="material-symbol"], [data-icon], svg, use'))
                        .map(icon => [
                          icon.textContent,
                          icon.getAttribute('aria-label'),
                          icon.getAttribute('data-icon'),
                          icon.getAttribute('href'),
                          icon.getAttribute('xlink:href')
                        ].filter(value => typeof value === 'string').join(' '))
                        .join(' ').toLocaleLowerCase();
                      const visibleText = (element.textContent || '').trim().toLocaleLowerCase();
                      const sendLabel = /\bsend\b|\bsubmit\b|gửi|envoyer|senden|enviar|invia|送信|전송|发送/;
                      const sendIcon = /\bsend\b|arrow[_ -]?upward|arrow[_ -]?up|north/;
                      const excluded = /\bstop\b|dừng|ngừng|mic|microphone|voice|audio|upload|attach|file|image|camera|tool|model|menu|emoji|more/;
                      const verticalOverlap = Math.min(promptRectangle.bottom, rectangle.bottom) -
                        Math.max(promptRectangle.top, rectangle.top);
                      const nearRightEdge = x >= promptRectangle.right - 180 &&
                        x <= promptRectangle.right + 220;
                      const smallControl = rectangle.width <= 96 && rectangle.height <= 96;
                      const metadata = `${accessibleLabel} ${testId} ${className} ${iconMetadata} ${visibleText}`;
                      const semanticScore = sendLabel.test(`${accessibleLabel} ${testId}`) ? 12
                        : sendIcon.test(iconMetadata) ? 10
                        : sendLabel.test(`${className} ${visibleText}`) ? 8
                        : element instanceof HTMLButtonElement && element.type === 'submit' ? 6 : 0;
                      const structuralScore = semanticScore === 0 && !excluded.test(metadata) &&
                        verticalOverlap > 0 && nearRightEdge && smallControl ? 1 : 0;
                      const score = semanticScore + structuralScore;
                      const hit = document.elementFromPoint(x, y);
                      return { element, x, y, score, semanticScore,
                        distance: horizontalDistance + verticalDistance,
                        horizontalDistance, verticalDistance,
                        visible: rectangle.width > 0 && rectangle.height > 0 &&
                          x >= 0 && x < innerWidth && y >= 0 && y < innerHeight &&
                          !!hit && (hit === element || element.contains(hit)),
                      };
                    })
                    .filter(candidate => candidate.visible && candidate.score > 0 &&
                      candidate.horizontalDistance < 200 && candidate.verticalDistance < 150 &&
                      !candidate.element.disabled &&
                      candidate.element.getAttribute('aria-disabled') !== 'true')
                    .sort((left, right) => right.score - left.score ||
                      left.distance - right.distance || right.x - left.x);
                  if (!buttons.length) return null;
                  buttons[0].element.setAttribute(
                    'data-agent-local-web-send-attempt', attemptMarker);
                  return buttons[0].element;
                })()
                """;
            var evaluation = await client.SendCommandAsync(
                "Runtime.evaluate",
                new { expression, returnByValue = false },
                cancellationToken).ConfigureAwait(false);
            if (evaluation.TryGetProperty("result", out var result) &&
                result.TryGetProperty("objectId", out var objectIdElement) &&
                !string.IsNullOrWhiteSpace(objectIdElement.GetString()))
            {
                var description = await client.SendCommandAsync(
                    "DOM.describeNode",
                    new { objectId = objectIdElement.GetString() },
                    cancellationToken).ConfigureAwait(false);
                var backendNodeId = description.GetProperty("node")
                    .GetProperty("backendNodeId").GetInt32();
                await ClickBackendNodeAsync(backendNodeId, cancellationToken).ConfigureAwait(false);
                if (await WaitForSubmissionAsync(baselineMarker, cancellationToken)
                        .ConfigureAwait(false))
                {
                    return true;
                }

                await client.SendCommandAsync(
                    "DOM.focus",
                    new { backendNodeId },
                    cancellationToken).ConfigureAwait(false);
                await PressSpaceAsync(cancellationToken).ConfigureAwait(false);
                if (await WaitForSubmissionAsync(baselineMarker, cancellationToken)
                        .ConfigureAwait(false))
                {
                    return true;
                }
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken)
                .ConfigureAwait(false);
        }

        return false;
    }

    private async Task<bool> TryStopGenerationAsync(CancellationToken cancellationToken)
    {
        const string expression = """
            (() => {
              const selector = [
                'button[aria-label*="stop" i]',
                'button[aria-label*="dừng" i]',
                'button[aria-label*="ngừng" i]',
                '[data-test-id*="stop" i]'
              ].join(',');
              return Array.from(document.querySelectorAll(selector)).find(element => {
                const rectangle = element.getBoundingClientRect();
                const style = getComputedStyle(element);
                return rectangle.width > 0 && rectangle.height > 0 &&
                  style.visibility !== 'hidden' && style.display !== 'none';
              }) || null;
            })()
            """;
        try
        {
            var evaluation = await client.SendCommandAsync(
                "Runtime.evaluate",
                new { expression, returnByValue = false },
                cancellationToken).ConfigureAwait(false);
            if (!evaluation.TryGetProperty("result", out var result) ||
                !result.TryGetProperty("objectId", out var objectIdElement) ||
                string.IsNullOrWhiteSpace(objectIdElement.GetString()))
            {
                return false;
            }

            var description = await client.SendCommandAsync(
                "DOM.describeNode",
                new { objectId = objectIdElement.GetString() },
                cancellationToken).ConfigureAwait(false);
            var backendNodeId = description.GetProperty("node")
                .GetProperty("backendNodeId").GetInt32();
            await ClickBackendNodeAsync(backendNodeId, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (
            exception is GeminiWebSessionException or KeyNotFoundException or InvalidOperationException)
        {
            return false;
        }
    }

    private async Task ClickBackendNodeAsync(
        int backendNodeId,
        CancellationToken cancellationToken)
    {
        await client.SendCommandAsync(
            "DOM.scrollIntoViewIfNeeded",
            new { backendNodeId },
            cancellationToken).ConfigureAwait(false);
        var box = await client.SendCommandAsync(
            "DOM.getBoxModel",
            new { backendNodeId },
            cancellationToken).ConfigureAwait(false);
        var border = box.GetProperty("model").GetProperty("border")
            .EnumerateArray().Select(item => item.GetDouble()).ToArray();
        if (border.Length != 8)
        {
            throw new GeminiWebSessionException(
                GeminiWebFailureKind.Compatibility,
                "Gemini exposed an invalid control boundary.");
        }

        var x = (border[0] + border[2] + border[4] + border[6]) / 4;
        var y = (border[1] + border[3] + border[5] + border[7]) / 4;
        await client.SendCommandAsync(
            "Input.dispatchMouseEvent",
            new
            {
                type = "mouseMoved",
                x,
                y,
                button = "none",
                buttons = 0,
                pointerType = "mouse"
            },
            cancellationToken).ConfigureAwait(false);
        await client.SendCommandAsync(
            "Input.dispatchMouseEvent",
            new
            {
                type = "mousePressed",
                x,
                y,
                button = "left",
                buttons = 1,
                clickCount = 1,
                pointerType = "mouse"
            },
            cancellationToken).ConfigureAwait(false);
        await client.SendCommandAsync(
            "Input.dispatchMouseEvent",
            new
            {
                type = "mouseReleased",
                x,
                y,
                button = "left",
                buttons = 0,
                clickCount = 1,
                pointerType = "mouse"
            },
            cancellationToken).ConfigureAwait(false);
    }

    private async Task PressSpaceAsync(CancellationToken cancellationToken)
    {
        await client.SendCommandAsync(
            "Input.dispatchKeyEvent",
            new
            {
                type = "rawKeyDown",
                key = " ",
                code = "Space",
                windowsVirtualKeyCode = 32,
                nativeVirtualKeyCode = 32
            },
            cancellationToken).ConfigureAwait(false);
        await client.SendCommandAsync(
            "Input.dispatchKeyEvent",
            new
            {
                type = "keyUp",
                key = " ",
                code = "Space",
                windowsVirtualKeyCode = 32,
                nativeVirtualKeyCode = 32
            },
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> WaitForSubmissionAsync(
        string baselineMarker,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            if (await HasSubmissionStartedAsync(baselineMarker, cancellationToken)
                    .ConfigureAwait(false))
            {
                return true;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken)
                .ConfigureAwait(false);
        }

        return false;
    }

    private async Task<bool> HasSubmissionStartedAsync(
        string baselineMarker,
        CancellationToken cancellationToken)
    {
        var marker = JsonSerializer.Serialize(baselineMarker);
        var expression = $$"""
            (() => {
              const promptSelector = [
                'rich-textarea [contenteditable="true"]',
                '.ql-editor[contenteditable="true"]',
                '[contenteditable="true"][role="textbox"]',
                '[contenteditable="plaintext-only"][role="textbox"]',
                'textarea[role="textbox"]',
                'textarea[aria-label*="prompt" i]',
                'textarea[placeholder*="Gemini" i]'
              ].join(',');
              const responseSelector = 'model-response, [data-test-id="model-response"], .model-response-text, .response-container-content';
              const marker = {{marker}};
              const prompts = Array.from(document.querySelectorAll(promptSelector)).filter(element => {
                const rectangle = element.getBoundingClientRect();
                const style = getComputedStyle(element);
                return rectangle.width > 0 && rectangle.height > 0 &&
                  style.visibility !== 'hidden' && style.display !== 'none';
              });
              const prompt = prompts.find(element =>
                element.getAttribute('data-agent-local-web-composer') === marker) ||
                prompts.sort((left, right) =>
                  right.getBoundingClientRect().bottom - left.getBoundingClientRect().bottom)[0];
              const promptValue = prompt
                ? ('value' in prompt ? prompt.value : prompt.innerText || prompt.textContent || '')
                : '';
              const hasNewResponse = Array.from(document.querySelectorAll(responseSelector))
                .some(element => element.getAttribute('data-agent-local-web-baseline') !== marker);
              const hasStopControl = Array.from(document.querySelectorAll(
                'button[aria-label*="stop" i], button[aria-label*="dừng" i], button[aria-label*="ngừng" i], [data-test-id*="stop" i]'))
                .some(element => {
                  const rectangle = element.getBoundingClientRect();
                  return rectangle.width > 0 && rectangle.height > 0;
                });
              return promptValue.length <= 1 || hasNewResponse || hasStopControl;
            })()
            """;
        var evaluation = await client.SendCommandAsync(
            "Runtime.evaluate",
            new { expression, returnByValue = true },
            cancellationToken).ConfigureAwait(false);
        return evaluation.TryGetProperty("result", out var result) &&
            result.TryGetProperty("value", out var value) &&
            value.ValueKind == JsonValueKind.True;
    }

    private async Task MarkExistingResponsesAsync(
        string baselineMarker,
        CancellationToken cancellationToken)
    {
        var marker = JsonSerializer.Serialize(baselineMarker);
        var expression = $$"""
            (() => {
              const selector = 'model-response, [data-test-id="model-response"], .model-response-text, .response-container-content';
              const marker = {{marker}};
              const candidates = Array.from(document.querySelectorAll(selector));
              candidates.forEach(element => element.setAttribute('data-agent-local-web-baseline', marker));
              return candidates.length;
            })()
            """;
        await client.SendCommandAsync(
            "Runtime.evaluate",
            new { expression, returnByValue = true },
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<int?> FindPromptBackendNodeIdAsync(CancellationToken cancellationToken)
    {
        const string expression = """
            (() => {
              const selector = [
                'rich-textarea [contenteditable="true"]',
                '.ql-editor[contenteditable="true"]',
                '[contenteditable="true"][role="textbox"]',
                '[contenteditable="plaintext-only"][role="textbox"]',
                'textarea[role="textbox"]',
                'textarea[aria-label*="prompt" i]',
                'textarea[placeholder*="Gemini" i]'
              ].join(',');
              const candidates = Array.from(document.querySelectorAll(selector))
                .map(element => {
                  const rectangle = element.getBoundingClientRect();
                  const style = getComputedStyle(element);
                  const score = element.closest('rich-textarea') ? 5
                    : element.classList.contains('ql-editor') ? 4
                    : element.getAttribute('role') === 'textbox' ? 3 : 1;
                  return { element, rectangle, score, style };
                })
                .filter(candidate => candidate.rectangle.width > 0 &&
                  candidate.rectangle.height > 0 &&
                  candidate.style.visibility !== 'hidden' &&
                  candidate.style.display !== 'none' &&
                  candidate.element.getAttribute('aria-disabled') !== 'true')
                .sort((left, right) => right.score - left.score ||
                  right.rectangle.bottom - left.rectangle.bottom);
              return candidates.length ? candidates[0].element : null;
            })()
            """;
        var evaluation = await client.SendCommandAsync(
            "Runtime.evaluate",
            new { expression, returnByValue = false },
            cancellationToken).ConfigureAwait(false);
        if (!evaluation.TryGetProperty("result", out var result) ||
            !result.TryGetProperty("objectId", out var objectIdElement))
        {
            return null;
        }

        var objectId = objectIdElement.GetString();
        if (string.IsNullOrWhiteSpace(objectId))
        {
            return null;
        }

        var description = await client.SendCommandAsync(
            "DOM.describeNode",
            new { objectId },
            cancellationToken).ConfigureAwait(false);
        try
        {
            return description.GetProperty("node").GetProperty("backendNodeId").GetInt32();
        }
        catch (Exception exception) when (
            exception is KeyNotFoundException or InvalidOperationException)
        {
            throw new GeminiWebSessionException(
                GeminiWebFailureKind.Compatibility,
                "Gemini's prompt element no longer exposes a supported DOM node.",
                innerException: exception);
        }
    }

    private async Task<IReadOnlyList<string>> ReadNewResponsesAsync(
        string baselineMarker,
        CancellationToken cancellationToken)
    {
        var marker = JsonSerializer.Serialize(baselineMarker);
        var expression = $$"""
            (() => {
              const selectors = [
                '.model-response-text',
                '[data-test-id="model-response"]',
                'model-response',
                '.response-container-content'
              ];
              const marker = {{marker}};
              for (const selector of selectors) {
                const texts = Array.from(document.querySelectorAll(selector))
                  .filter(element => element.getAttribute('data-agent-local-web-baseline') !== marker)
                  .map(element => (element.innerText || '').trim())
                  .filter(text => text.length > 0);
                if (texts.length > 0) return texts;
              }
              return [];
            })()
            """;
        var evaluation = await client.SendCommandAsync(
            "Runtime.evaluate",
            new { expression, returnByValue = true },
            cancellationToken).ConfigureAwait(false);

        try
        {
            var value = evaluation.GetProperty("result").GetProperty("value");
            return value.EnumerateArray()
                .Select(item => item.GetString() ?? string.Empty)
                .Where(item => item.Length > 0)
                .ToArray();
        }
        catch (Exception exception) when (exception is KeyNotFoundException or InvalidOperationException)
        {
            throw new GeminiWebSessionException(
                GeminiWebFailureKind.Compatibility,
                "Gemini response elements no longer match the supported page structure.",
                innerException: exception);
        }
    }

    private async Task<bool> IsGenerationInProgressAsync(CancellationToken cancellationToken)
    {
        const string expression = """
            (() => {
              const stopSelector = [
                'button[aria-label*="stop" i]',
                'button[aria-label*="dừng" i]',
                'button[aria-label*="ngừng" i]',
                '[data-test-id*="stop" i]'
              ].join(',');
              return Array.from(document.querySelectorAll(stopSelector)).some(element => {
                  const rectangle = element.getBoundingClientRect();
                  const style = getComputedStyle(element);
                  return rectangle.width > 0 && rectangle.height > 0 &&
                    style.visibility !== 'hidden' && style.display !== 'none';
                });
            })()
            """;
        var evaluation = await client.SendCommandAsync(
            "Runtime.evaluate",
            new { expression, returnByValue = true },
            cancellationToken).ConfigureAwait(false);
        return evaluation.TryGetProperty("result", out var result) &&
            result.TryGetProperty("value", out var value) &&
            value.ValueKind == JsonValueKind.True;
    }

    private async Task<PageDiagnostics> ReadDiagnosticsAsync(
        string baselineMarker,
        CancellationToken cancellationToken)
    {
        var marker = JsonSerializer.Serialize(baselineMarker);
        var expression = $$"""
            (() => {
              const responseSelector = 'model-response, [data-test-id="model-response"], .model-response-text, .response-container-content';
              const promptSelector = 'rich-textarea [contenteditable="true"], .ql-editor[contenteditable="true"], [contenteditable="true"][role="textbox"], textarea[aria-label*="prompt" i], textarea[placeholder*="Gemini" i]';
              const marker = {{marker}};
              const responses = Array.from(document.querySelectorAll(responseSelector));
              const prompt = Array.from(document.querySelectorAll(promptSelector)).find(element => {
                const rectangle = element.getBoundingClientRect();
                return rectangle.width > 0 && rectangle.height > 0;
              });
              const promptValue = prompt
                ? ('value' in prompt ? prompt.value : prompt.innerText || '')
                : '';
              const promptRectangle = prompt?.getBoundingClientRect();
              const sendButtons = Array.from(document.querySelectorAll(
                '[data-test-id="send-button"], button[aria-label*="send" i], button[mattooltip*="send" i], button.send-button, .send-button button'));
              const nearButtons = promptRectangle
                ? Array.from(document.querySelectorAll('button')).filter(element => {
                    const rectangle = element.getBoundingClientRect();
                    const horizontalDistance = Math.max(
                      promptRectangle.left - rectangle.right,
                      rectangle.left - promptRectangle.right,
                      0);
                    const verticalDistance = Math.max(
                      promptRectangle.top - rectangle.bottom,
                      rectangle.top - promptRectangle.bottom,
                      0);
                    return rectangle.width > 0 && rectangle.height > 0 &&
                      horizontalDistance < 200 && verticalDistance < 150;
                  })
                : [];
              return {
                responseNodes: responses.length,
                newResponseNodes: responses.filter(element =>
                  element.getAttribute('data-agent-local-web-baseline') !== marker).length,
                composerCharacters: promptValue.length,
                sendButtons: sendButtons.length,
                nearButtons: nearButtons.length,
                enabledNearButtons: nearButtons.filter(element =>
                  !element.disabled && element.getAttribute('aria-disabled') !== 'true').length,
                nearButtonMetadata: nearButtons.map(element => [
                  element.getAttribute('aria-label') || '',
                  element.getAttribute('data-test-id') || '',
                  String(element.className || '').slice(0, 80)
                ].join('/')).join('|').slice(0, 500),
                stopControls: document.querySelectorAll(
                  'button[aria-label*="stop" i], button[aria-label*="dừng" i], button[aria-label*="ngừng" i], [data-test-id*="stop" i]').length,
                busyElements: document.querySelectorAll('[aria-busy="true"]').length
              };
            })()
            """;
        var evaluation = await client.SendCommandAsync(
            "Runtime.evaluate",
            new { expression, returnByValue = true },
            cancellationToken).ConfigureAwait(false);
        try
        {
            var value = evaluation.GetProperty("result").GetProperty("value");
            return new PageDiagnostics(
                value.GetProperty("responseNodes").GetInt32(),
                value.GetProperty("newResponseNodes").GetInt32(),
                value.GetProperty("composerCharacters").GetInt32(),
                value.GetProperty("sendButtons").GetInt32(),
                value.GetProperty("nearButtons").GetInt32(),
                value.GetProperty("enabledNearButtons").GetInt32(),
                value.GetProperty("nearButtonMetadata").GetString() ?? string.Empty,
                value.GetProperty("stopControls").GetInt32(),
                value.GetProperty("busyElements").GetInt32());
        }
        catch (Exception diagnosticsException) when (
            diagnosticsException is KeyNotFoundException or InvalidOperationException)
        {
            throw new GeminiWebSessionException(
                GeminiWebFailureKind.Compatibility,
                "Gemini no longer exposes the expected sanitized page diagnostics.",
                innerException: diagnosticsException);
        }
    }

    private sealed record PageDiagnostics(
        int ResponseNodes,
        int NewResponseNodes,
        int ComposerCharacters,
        int SendButtons,
        int NearButtons,
        int EnabledNearButtons,
        string NearButtonMetadata,
        int StopControls,
        int BusyElements);

}
