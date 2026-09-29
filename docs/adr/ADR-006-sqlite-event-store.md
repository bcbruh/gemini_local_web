# ADR-006: SQLite app state and event store

- Status: Accepted
- Date: 2026-09-29

## Context

The local app must restore its last workspace and later restore conversations and runs. The browser also needs an event stream that can reconnect without treating in-memory UI state as authoritative. Credentials and unbounded tool output must never enter this store.

## Decision

Use one SQLite database at `%LocalAppData%\AgentLocalWeb\Data\agent-local-web.db`. The backend owns all database access and serializes writes. Schema changes use a forward-only `schema_migrations` table. Version 1 contains the singleton `app_state` row and sequenced `app_events`; version 2 adds the active conversation plus ordered messages.

Enable WAL mode and foreign-key enforcement. Each public event has a monotonically increasing database sequence, UTC timestamp, bounded JSON-object payload and optional run ID. SSE sends the sequence as its event ID; reconnect reads rows strictly after the supplied cursor before waiting for new commits.

Saving a workspace and its `workspace.selected` event occurs in one transaction. A saved workspace is restored only after it is revalidated as an existing absolute local directory. UNC/device paths and volume roots are rejected; invalid stale state is cleared instead of silently selecting another directory.

## Data boundaries

- Store application state, sanitized messages, run/tool metadata and bounded redacted event payloads here as later migrations add them.
- Keep Gemini cookies, access tokens and other credentials outside SQLite.
- Keep large artifacts in a bounded artifact area and store only references and hashes in SQLite.
- The default data directory is separate from the browser profile and from any selected workspace.

## Consequences

- Snapshot reads and event replay survive process restarts.
- A single writer avoids competing transaction ownership inside the modular monolith.
- Migration backup/recovery policy is still required before the first destructive migration.
- Retention and compaction are required before storing high-volume run output.
