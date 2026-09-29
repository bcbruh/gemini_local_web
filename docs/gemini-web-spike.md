# Gemini Web feasibility spike

## Purpose

Resolve ADR-002 without coupling experimental browser/authentication code to the runtime. This spike must use a test account/profile with no unrelated browsing data.

## Safety constraints

- Never ask the user to paste cookies or DevTools output.
- Never commit browser profiles, cookies, captures, request headers or response content tied to a real account.
- Disable verbose HTTP/browser logging around authentication.
- Store temporary profiles outside the repository and document cleanup.
- Do not put workspace tools or filesystem access in the spike.
- Use a normal visible browser and let the user complete Google sign-in. Do not bypass CAPTCHA, account verification or anti-abuse controls.
- Phase A must not scrape hidden endpoints, copy cookies or automate Gemini page DOM.

## Experiments

| ID | Experiment | Evidence required |
|---|---|---|
| GWS-01 | Launch installed browser with an isolated profile | Supported browser/version, profile path behavior, clean shutdown |
| GWS-02 | Detect successful Gemini login | Stable signal and timeout/error behavior |
| GWS-03 | Send a prompt and collect one response | Normalized request/response fixture with account data removed |
| GWS-04 | Restart and reuse the isolated session | No manual cookie step; credential location documented |
| GWS-05 | Expire/revoke session and reconnect | Typed `SessionExpired` transition and recovery |
| GWS-06 | Disconnect and clean local session | Defined data deletion and verification |
| GWS-07 | Simulate layout/protocol change | Typed compatibility failure; no guessed action |

## Phase A: isolated browser profile

The `tools/AgentLocalWeb.GeminiWeb.Spike` utility discovers a supported installed browser and can launch a dedicated profile under the current user's local application-data directory. It does not inspect the user's normal browser profile and does not read cookies.

```powershell
$dotnet = Join-Path (Get-Location) '.tools/dotnet/dotnet.exe'
& $dotnet run --project tools/AgentLocalWeb.GeminiWeb.Spike -- discover
& $dotnet run --project tools/AgentLocalWeb.GeminiWeb.Spike -- launch-login chrome
& $dotnet run --project tools/AgentLocalWeb.GeminiWeb.Spike -- launch-debug chrome
& $dotnet run --project tools/AgentLocalWeb.GeminiWeb.Spike -- probe-tabs chrome
& $dotnet run --project tools/AgentLocalWeb.GeminiWeb.Spike -- roundtrip chrome
& $dotnet run --project tools/AgentLocalWeb.GeminiWeb.Spike -- adapter-roundtrip chrome
```

`launch-login` is intentionally interactive and must only be invoked when a user is ready to complete sign-in in the visible browser.

`launch-debug` requires the isolated Chrome instance to be fully closed first. It enables a random loopback DevTools port for the spike profile. `probe-tabs` only verifies that the endpoint and a `gemini.google.com` page exist; it does not request cookies, storage, network headers or page content.

Chrome's official guidance requires a non-default user-data directory for remote debugging from Chrome 136 onward. The spike follows that isolation requirement: <https://developer.chrome.com/blog/remote-debugging-port>.

## Phase B: minimal round-trip

The round-trip probe uses the Chrome DevTools Protocol over the random loopback port. It locates the prompt input through the accessibility tree, types a random non-sensitive nonce and checks only whether that nonce occurs in the resulting page. It does not request browser cookies, storage or network traffic and does not write page content to logs.

`adapter-roundtrip` exercises the production `GeminiWebBrain` path against the same isolated profile. It also uses a random nonce and reports only pass/fail; running it creates one harmless test conversation in the signed-in Gemini account.

## Exit decision

- **Go:** all required flows work without raw credential exposure and have a maintainable failure signal.
- **Revise:** flow works but needs an isolated sidecar or explicit browser dependency.
- **Stop:** integration requires unsafe credential handling, is too brittle to diagnose, or cannot be used within acceptable terms/account risk.
