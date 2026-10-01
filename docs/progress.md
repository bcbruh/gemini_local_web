# Implementation progress

## Iteration 1 — Gemini Web feasibility and production adapter

Last updated: 2026-09-30

Manual use on 2026-09-30 now passes ordinary first and follow-up messages after compatibility
repairs for prompt submission, streamed-response completion, rendered response extraction and
stale CDP reconnect. Long-turn latency remains open. A separate context-efficiency issue is now
confirmed: every Brain call sends a rebuilt bounded snapshot while the same Gemini Web chat also
retains all earlier snapshots, duplicating history on the remote side. Design analysis is recorded
below; no context-transport change or further live prompt was made after quota exhaustion.

| Check | Status | Evidence / next action |
|---|---|---|
| Brain abstraction and Fake Brain | Passed | Solution builds; offline contract tests pass |
| GWS-01: isolated installed-browser launch | Passed | Chrome 154 launched with a dedicated profile under local application data |
| GWS-02: interactive Gemini login | Passed | User completed manual login; the app later reached an authenticated prompt textbox through loopback CDP |
| GWS-03: send prompt and normalize response | Passed (production adapter) | `GeminiWebBrain` submitted a random nonce and returned the expected normalized final response without logging page content |
| GWS-04: restart and reuse session | Passed | Isolated Chrome was fully closed and reopened; user confirmed Gemini remained signed in |
| GWS-05: expiry and reconnect | Partial | Missing prompt maps to `ReconnectRequired`/`SessionExpired` in contract tests; live account expiry/revocation is not yet exercised |
| GWS-06: disconnect and cleanup | Passed (contract) | Transport and adapter-owned Chrome are closed; the isolated profile is intentionally retained, and account removal is deferred to a separate explicit operation |
| GWS-07: compatibility failure | Passed (contract) | Invalid CDP/accessibility/response structures fail closed as `CompatibilityFailure`; no guessed response is returned |

No account identifier, browser cookie, request header or credential is recorded in the repository or application logs.

The DevTools loopback probe and production `GeminiWebBrain` round-trip passed on Chrome 154. This is a Go for local development with the isolated Chrome profile strategy. Live expiry/revocation and the terms/account-risk review remain gates for external beta, not for offline app-shell development.

Automated result: 15 contract tests pass without a Gemini account or network dependency.

## Iteration 2 — Local app shell

| Slice | Status | Evidence / next action |
|---|---|---|
| Loopback host and random port | Passed | Process smoke test listened only on `127.0.0.1` with an OS-assigned port |
| Browser bootstrap/session | Implemented | One-use expiring bootstrap token, HttpOnly strict cookie and independent CSRF token |
| Host/origin defenses | Passed (unit + smoke) | Spoofed Host and foreign Origin returned HTTP 403; unauthenticated API returned 401 |
| Minimal UI shell | Implemented | Static bootstrap page displays Brain/workspace state, activity events and can connect Fake Brain |
| React/Vite frontend | Passed (build + component test + smoke) | TypeScript source builds to fingerprinted AppHost assets; bootstrap/restored state has a jsdom component test |
| Workspace selection/persistence | Passed (unit + startup smoke) | Native Windows folder picker; canonical existing root is restored from local app state and stale roots are cleared |
| SQLite migration and app state | Passed (unit + startup smoke) | Versioned schema at `%LocalAppData%\AgentLocalWeb\Data`; WAL, serialized writes and atomic workspace/event commit |
| SSE event stream | Implemented (unit) | Authenticated stream replays by sequence cursor, emits event IDs and heartbeats, then resumes from committed events |
| Persisted Fake Brain conversation | Passed (unit + migration smoke) | SQLite v2 stores ordered user/assistant messages; restart restores the active conversation and SSE announces changes by ID/role only |
| End-to-end HTTP security/event flow | Passed (real Kestrel integration) | One-use bootstrap, cookie attributes, Host/Origin rejection, CSRF enforcement, authenticated SSE replay and `Last-Event-ID` reconnect run against an isolated temporary database |

Automated result after this slice: 53 tests pass: 52 backend tests across Brain, AppHost, Workspace and Persistence, plus 1 React component test. The production frontend build also passes.

Iteration 2 is complete. Iteration 3 next introduces the run state machine, protocol V1 and read-only workspace tools.

## Iteration 3 — Read-only agent loop

| Slice | Status | Evidence / next action |
|---|---|---|
| Protocol V1 parser | Passed (unit) | Exact JSON envelopes only; rejects prose, duplicate/unknown fields, unsupported protocol/type/tool and oversized input |
| Run state machine | Passed (unit) | Valid transitions, terminal states, opt-in protocol retry budget, tool-turn budget, duplicate request IDs and cancellation are enforced; production defaults to no automatic protocol retry |
| Workspace boundary | Passed (Windows integration) | Rejects traversal, absolute/UNC/device/ADS paths and outside junctions; opened file handles are checked against the final physical root |
| Read-only workspace primitives | Passed (unit + filesystem integration) | Bounded list/read/search, line ranges, SHA-256 snapshot metadata, ignore rules and binary/large/secret blocking |
| Typed read-only tools | Passed (unit) | `list_directory`, `read_file`, `search_files` and `search_text` use strict argument schemas and typed bounded results |
| Fake Brain read-only loop | Passed (unit) | Scripted tool request is executed once, returned as a correlated observation and followed by a protocol final response |
| Run persistence and recovery | Passed (unit + migration) | SQLite v3 stores protocol/prompt versions, transitions, terminal/error/cancel state; startup marks unfinished runs `interrupted` |
| Tool-call audit and activity | Passed (unit + real Kestrel) | SQLite v4 records read-only tool request/result status with SSE events; startup interrupts unfinished calls; `/api/state` and UI show bounded tool metadata without arguments, file content or model request IDs |
| App/API/UI integration | Passed (real Kestrel integration) | Conversation uses the agent loop; SSE exposes durable run transitions; state exposes the latest run; cancel stops an active request; UI renders status/activity and a cancel action |
| Fake Brain workspace E2E | Passed (real Kestrel + filesystem) | Authenticated HTTP flow selects a workspace, executes `read_file`, returns a correlated observation, persists the final answer and completes the run |
| Context budgeting | Passed (unit) | Requests are capped at 512 KiB, retain the newest bounded conversation history, exclude persisted system-role content and fail before contacting the Brain when the current turn cannot fit |
| Typed error mapping and UX | Passed (unit + real Kestrel + component) | Brain failures map to stable run errors and persisted categories; private adapter diagnostics are not returned, and the UI renders actionable Vietnamese guidance |
| Gemini provider selection | Implemented | `--brain=gemini` selects the production adapter; Fake Brain remains the deterministic default for offline development |
| Gemini read-only E2E | Passed (live prompt v4) | The production adapter completed `Gemini → read_file → observation → final` against an isolated temporary workspace. The final message contained the exact token read from `proof.txt`; the harness exited successfully and removed the workspace. |

Current automated result: 119 tests pass: 117 backend tests across Brain, Agent Core, Tools, AppHost, Workspace and Persistence, plus 2 React component tests. The production frontend build also passes.

## Iteration 4 — Safe edit

| Slice | Status | Evidence / next action |
|---|---|---|
| Patch contract and ADR | Passed | ADR-007 selects bounded exact replacements for existing UTF-8 files; protocol prompt/version and documentation include `prepare_patch`/`apply_patch` |
| Patch validation and preview | Passed (offline) | Paths, secret-like/binary/large files, expected SHA-256, unique exact matches and batch limits are validated before a bounded diff/action hash is produced |
| Approval persistence and state machine | Passed (offline) | SQLite v5 stores requested/approved/rejected/expired decisions; the run enters `waiting_for_approval`; restart expires unfinished approvals |
| Diff review UI | Passed (component + real HTTP) | Pending diff, exact action prefix, Apply/Reject actions and permission selector are rendered; authenticated decision API requires the matching action hash |
| Safe apply and stale protection | Passed (filesystem + real HTTP) | All files stage before replacement, backups support compensating batch rollback, physical targets and hashes are rechecked, and stale input prevents every write |
| Permission modes | Passed (unit) | `read_only` denies patch preparation, `ask_before_changes` pauses for approval, and `auto_edit_workspace` still runs all hard validation before apply |
| Fake Brain edit E2E | Passed (real Kestrel + filesystem) | `prepare_patch → diff → waiting approval → wrong hash rejected → exact approval → apply → final` completes and changes only the selected workspace file |
| Gemini edit E2E | Not run | Read-only prompt v4 now passes live, but no live edit request has been authorized or needed for this slice |

Current automated result after the safe-edit slice: 129 checks pass: 126 backend tests
across Brain, Agent Core, Tools, AppHost, Workspace and Persistence, plus 3 React component
tests. The production frontend build also passes.

## Post-MVP UI simplification and workspace picker repair

| Slice | Status | Evidence / next action |
|---|---|---|
| Fixed-height chat shell | Passed (component + production build) | The application is now a white, single-viewport chat layout; the conversation and left activity sidebar scroll independently, while the composer remains fixed at the bottom |
| New-message positioning | Implemented | Conversation updates scroll the message viewport to the newest message without increasing the document height |
| Simplified controls | Implemented | Brain, workspace and permission controls live in a compact sidebar; run/tool/event activity remains visible in its own bounded scroll area |
| Workspace picker foreground ownership | Implemented (manual smoke pending) | The Windows picker captures the foreground browser handle and opens as its owned modal dialog instead of being able to hide behind Chrome |
| Independent pending states | Passed (component) | Waiting for the native workspace picker disables only its own button; Brain and chat controls remain usable |
| Explicit context/session boundary | Passed (persistence + agent + HTTP + component) | **Phiên mới** atomically activates an empty conversation while retaining old local history. The next Brain request excludes every message from the previous session; the action is blocked while a run is active. |
| Slow-connect state and measurement | Passed (HTTP + component) | The UI switches from `Disconnected` to `Connecting` before the long request returns. Durable content-free `brain.connection_started` and terminal connection events provide timestamps for measuring startup without logging chat or credentials. |
| Background Chrome mode | Planned / documented | ADR-002 selects adapter-owned Chrome running hidden after initial login, with an explicit reveal action; implementation has not started |
| Latest Gemini compatibility retest | Passed through full read-only agent loop | Prompt v4 completed a production `read_file` tool request, correlated observation and final response in one live harness run. Automatic protocol correction remains disabled by default, so malformed output cannot silently create another visible Gemini prompt. |
| Manual multi-turn Gemini compatibility | Partial / usable | User confirmed the first message and later follow-up flow can complete. Repairs now prefer Enter submission, avoid stale send-button nodes, wait for a closing JSON object, read response `textContent`, narrowly normalize browser typography/rendered line breaks only when the result parses as one object, and discard a broken CDP transport on reconnect. Repeated reliability still needs a quota-restored soak. |
| Remote context growth | Design pending | Local context is bounded to 512 KiB and the newest 40 persisted messages, but each call currently resends that snapshot into one persistent Gemini Web chat. The remote chat therefore retains duplicate copies. Do not run more live tests until quota is restored; evaluate snapshot-once plus delta turns and bounded remote-chat rotation before implementation. |

Current automated result: 130 checks pass: 126 backend tests plus 4 React component tests.
Release build succeeds with zero warnings and zero errors. A manual picker smoke remains after
restarting the already-running AppHost so it loads the new backend assembly.

Latest checkpoint: 151 offline checks pass (139 backend, 6 React, 6 DOM regressions), and
the frontend production build passes. A newly authorized live prompt-v4 run completed the
full read-only agent tool loop successfully. The earlier duplicate visible prompt was an
automatic protocol correction, not a duplicate UI submission; prompt v4 keeps correction
disabled by default. An explicit new-session boundary now prevents unrelated older history
from entering later requests. Slow connection now displays `Connecting` and emits bounded timing
events; repeated live measurements and long-turn latency analysis remain open. Gemini edit E2E
and live expiry/revocation are also pending. Background Chrome is still planned.

Post-checkpoint targeted validation after the 2026-09-30 manual compatibility fixes: all 34
Gemini Brain contract tests and 7 Gemini DOM regressions pass, and `dotnet format
--verify-no-changes` passes. The last complete solution-wide checkpoint remains the 151-check
run above; no additional live Gemini request should be made until the user's quota is restored.

## Context transport analysis — implementation deferred

Current behavior is safe but wasteful. `AgentContextBuilder` rebuilds each Brain request from the
system prompt, up to the newest 40 user/assistant messages, the current user message and the
current run's tool exchanges, capped at 512 KiB. `GeminiWebPromptFormatter` serializes that whole
snapshot on every Brain call. Because `ChromeGeminiWebSession` keeps using the same Gemini Web
conversation, Gemini also retains every older full snapshot. This duplicates context, increases
latency and can exhaust the remote conversation substantially earlier than the local budget implies.

Recommended direction, not yet implemented:

1. Treat the local SQLite conversation and run state as the source of truth.
2. Hydrate a fresh remote Gemini chat once with the bounded snapshot.
3. While the requested message list extends the acknowledged snapshot exactly, send only the new
   suffix (new user turn or tool observation) and track a content-free prefix hash/turn counter.
4. On reconnect, navigation, local **Phiên mới**, prefix mismatch, or a conservative remote byte/
   turn threshold, rotate to a fresh Gemini chat and rehydrate the newest bounded local context.
5. Later add summary compaction for older local history; do not depend on undocumented Gemini
   context limits or silently drop current-run tool observations.

A simpler stateless alternative is a fresh Gemini chat for every Brain call. It is easier to reason
about and prevents remote duplication, but it adds navigation latency and creates many remote chat
entries. The snapshot-once/delta/rotation design is preferred if it can be covered by deterministic
offline state-machine tests before another live probe.
