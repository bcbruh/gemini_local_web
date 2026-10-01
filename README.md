# Gemini Local Agent

Local-first coding agent for Windows. Gemini Web supplies reasoning through an isolated brain adapter; all workspace access, policy decisions and tool execution remain in the local runtime.

The product requirements are in [`decribe.md`](./decribe.md), and the staged implementation plan is in [`plan.md`](./plan.md).

## Development status

The repository has a validated production Gemini adapter, a completed local app shell and read-only agent loop, and a completed offline safe-edit/approval slice. The production adapter has also passed one live prompt-v4 `read_file` tool loop; command execution and V1 hardening remain future work.

Implemented:

- Provider-neutral `IBrain` contract.
- Deterministic `FakeBrain` for offline development and tests.
- Production `GeminiWebBrain` adapter using a visible, isolated Chrome profile and loopback-only CDP.
- Typed authentication, network and compatibility failures with fail-closed behavior.
- Contract tests for connection, reconnect, response normalization, cleanup, error mapping, ordering and cancellation.
- Live smoke test that validated persistent Chrome login and the production adapter's nonce prompt/response path without extracting cookies.
- Loopback-only local host with one-time browser bootstrap, session cookie, CSRF and Host/Origin checks.
- Native Windows workspace picker with persisted/validated workspace restore.
- Versioned SQLite app state/event store and resumable authenticated SSE stream.
- React/TypeScript frontend with restored Fake Brain conversation and realtime activity.
- Real-loopback HTTP integration coverage for bootstrap, cookie/CSRF defenses and resumable SSE.
- Strict `local-agent/v1` parser and bounded run state machine with opt-in protocol retry, duplicate-call and cancellation controls.
- Sandboxed read-only workspace tools for list/read/file search/text search with junction checks and content limits.
- SQLite-backed run lifecycle with crash recovery, correlated SSE transitions and durable cancellation state.
- End-to-end HTTP agent loop with workspace tools, typed failures and an active-run cancel endpoint.
- Realtime UI run status/activity with cancellation; production Gemini selection through `--brain=gemini`.
- Structured patch preparation, hash-bound approval, stale-file protection, staged multi-file apply and compensating rollback.
- Three permission modes and an authenticated diff review/apply/reject flow.
- An explicit new-session boundary that keeps old history local but excludes it from later Brain context.

## Local build

The project uses .NET SDK 10.0.401. This workspace currently keeps a local SDK under `.tools/dotnet` (ignored by Git).

```powershell
$dotnet = Join-Path (Get-Location) '.tools/dotnet/dotnet.exe'
& $dotnet restore
& $dotnet build --no-restore
& $dotnet test --no-build
```

No Gemini credential is required for the offline test path.

Build and test the frontend after changing files in `src/AgentLocalWeb.Web`:

```powershell
Set-Location src/AgentLocalWeb.Web
npm install
npm test
npm run build
Set-Location ../..
```

The Vite production build writes fingerprinted assets into `src/AgentLocalWeb.AppHost/wwwroot`.

Run the current local shell with Fake Brain:

```powershell
& $dotnet run --project src/AgentLocalWeb.AppHost
```

Run with the production Gemini Web adapter and the isolated signed-in Chrome profile:

```powershell
& $dotnet run --project src/AgentLocalWeb.AppHost -- --brain=gemini
```

The host binds only to a random `127.0.0.1` port and opens a one-time bootstrap URL in the default browser. Pass `-- --no-browser` when running an HTTP smoke test.

Local state is stored under `%LocalAppData%\AgentLocalWeb\Data`. It contains no Gemini browser credential; the isolated browser profile remains in its separate directory.
