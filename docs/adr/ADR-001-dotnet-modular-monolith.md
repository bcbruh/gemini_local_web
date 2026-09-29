# ADR-001: .NET modular monolith

- Status: Accepted
- Date: 2026-09-29

## Context

The product must ship as a low-configuration Windows executable while keeping Gemini integration replaceable. It needs reliable cancellation, process control, local HTTP, SQLite and Windows credential protection.

## Decision

Use a modular monolith on .NET 10 with a React/TypeScript frontend embedded as static assets for production. Backend modules use project references and explicit interfaces. The composition root may reference implementations; domain modules depend only on abstractions.

Use a self-contained Windows publish for distribution. Browser UI uses the system browser; development-only E2E tooling is not shipped.

## Consequences

- A single local process simplifies packaging and recovery.
- Windows integration and process lifecycle APIs are directly available.
- Distribution size is larger than a framework-dependent build.
- Gemini-specific browser behavior may require an isolated sidecar. Such a sidecar may implement the adapter contract but may not absorb agent, tool or policy logic.
