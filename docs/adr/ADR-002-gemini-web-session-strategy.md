# ADR-002: Gemini Web authentication and session strategy

- Status: Accepted for local implementation; external beta remains gated
- Date: 2026-09-29

## Context

Gemini Web is not a stable agent API. Authentication details must not leak into the UI, logs or agent core. The desired UX is interactive browser login without asking users to copy cookies or tokens.

## Candidate approaches

1. Dedicated profile using an installed Edge/Chrome-compatible browser, with the session retained inside that profile.
2. Browser-assisted login followed by extraction of the minimum session material into a Windows-protected credential vault.
3. A separately distributed browser component only if installed-browser control cannot meet reliability and security requirements.

## Validation checklist

- First interactive login and detection of success.
- Send prompt and receive complete/streamed response.
- Restart app and reuse the session without exposing raw credentials.
- Detect expiry, reconnect and disconnect.
- Distinguish network, rate-limit, authentication and compatibility failures.
- Confirm browser/version dependencies and assess terms/account-risk before external beta.
- Produce sanitized response fixtures for adapter contract tests.

## Temporary decision

Use installed Google Chrome with an application-owned persistent profile and a visible user-controlled login flow. Launch Chrome with a random loopback DevTools port and the non-default `--user-data-dir`. Communicate only through Chrome DevTools Protocol; do not extract cookies or call hidden Gemini endpoints.

The spike validated browser discovery, manual login, restart/session reuse and loopback target discovery. The production `GeminiWebBrain` then passed a live nonce prompt/response round-trip. It maps missing authentication, network interruption and incompatible page/CDP structures to typed failures and fails closed. Offline development and CI continue to use `FakeBrain` or an injected fake Gemini session.

`DisconnectAsync` closes the adapter transport and a Chrome instance opened by that adapter, but retains the application-owned profile so restart can reuse the session. It never deletes browser data. A future explicit “Forget Gemini account” operation will own profile deletion after exact-path validation and separate user confirmation.

Live account expiry/revocation has not been forced against the user's account. That validation and the terms/account-risk review remain release gates before external beta.

Edge remains a possible fallback but is not validated for the production path yet.

## Decision update: background Chrome UX

The planned production UX remains the installed-Chrome/CDP strategy, but the adapter-owned
Chrome window should run in the background after authentication:

1. Show the isolated Chrome window for first-time sign-in, session recovery and explicit
   troubleshooting only.
2. After an authenticated prompt is confirmed, keep the Chrome process and isolated profile
   alive but hide its window; the local chat UI remains the primary interface.
3. Provide an explicit **Open Gemini window** action so the user can reveal the same window when
   login or manual inspection is required.
4. Never hide a window owned by the user's normal Chrome profile; window management is limited
   to the exact process launched by the adapter.
5. If background operation becomes unreliable, fail visibly and offer to reveal Gemini rather
   than silently retrying or bypassing browser controls.

WebView2 is not selected for the current architecture. It would require a new native host,
runtime/user-data-folder lifecycle management and a fresh authentication path while still
running Chromium processes and still depending on Gemini's changing DOM. It can be reconsidered
only if the product deliberately moves from the loopback web UI to a Windows desktop shell.

This decision changes window presentation only. Chrome remains required while `gemini-web` is
connected, and background mode does not solve the current `brain.compatibility` failure.

## Official-source review (2026-09-29)

- Google documents Chrome and Edge-compatible browsers as supported ways to sign in to the Gemini web app: <https://support.google.com/gemini/answer/13278668>.
- The same sign-in guidance says Google uses signals to verify that real users are behind accounts. The adapter must not attempt to bypass account verification or anti-abuse controls.
- The Gemini Apps Privacy Hub says prompts and shared content are processed under the Gemini Apps privacy terms and may be retained/reviewed depending on account and activity settings: <https://support.google.com/gemini/answer/13594961>.
- Google Terms prohibit automated access that violates machine-readable site instructions and describe account consequences for abusive scraping: <https://policies.google.com/terms>.

These sources do not constitute a stable browser automation API. Before external beta, the exact interaction method, applicable terms and current machine-readable instructions require a dedicated review. Until then, the spike is limited to a normal visible browser, an isolated user-controlled profile and no extraction of cookies or hidden web endpoints.
