# ADR-005: Local browser bootstrap and API session

- Status: Accepted for Iteration 2
- Date: 2026-09-29

## Context

The product UI is served from a random loopback port and opens in the user's system browser. Binding only to loopback is necessary but insufficient: another website or local process could still attempt requests to the API. The initial browser must receive access without placing a reusable secret in a query string, referrer or application log.

## Decision

- Kestrel binds to `127.0.0.1` on an operating-system-assigned port.
- Each application start generates a 256-bit bootstrap token valid for two minutes and one use.
- The browser receives that token in the URL fragment. Fragments are not sent in the HTTP request; bootstrap JavaScript exchanges it with the local API and immediately clears the fragment.
- A successful exchange creates an independent 256-bit session cookie and CSRF token. The cookie is `HttpOnly`, `SameSite=Strict` and scoped to `/`. It is not marked `Secure` because the loopback development endpoint uses HTTP.
- Every request must use the exact `127.0.0.1` Host. When an `Origin` header exists, its scheme, host and port must match the current loopback origin.
- API routes other than bootstrap require the session cookie. State-changing methods additionally require the CSRF header.
- Responses use a restrictive Content Security Policy, deny framing and suppress referrer data.
- The bootstrap/session values are never written to logs or persistence and are regenerated at every process start.

## Consequences

Refreshing an already authenticated tab works because it can request the CSRF token using its HttpOnly session cookie. A copied bootstrap URL stops working after its first successful exchange or after two minutes. Non-browser local clients without an `Origin` header still need the unguessable session credential; a future pairing API must be a separate explicit design.

Before external beta, integration tests must cover cookie attributes, bootstrap replay, cross-origin requests, Host spoofing and CSRF rejection through the real HTTP pipeline.
