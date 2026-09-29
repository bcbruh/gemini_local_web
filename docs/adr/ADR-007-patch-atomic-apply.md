# ADR-007: Structured patch, approval binding and batch apply

- Status: Accepted
- Date: 2026-09-29

## Context

The model must be able to propose edits without receiving a direct filesystem write
primitive. The runtime needs a deterministic preview, must reject stale files, and must
bind an approval to exactly the bytes that will be written. Parsing arbitrary unified
diff text would add ambiguous hunk and line-ending behavior at the trust boundary.

## Decision

`prepare_patch` accepts a bounded structured exact-replacement document. Each existing
UTF-8 file supplies its workspace-relative path, the SHA-256 observed by `read_file`, and
one or more non-empty `old_text`/`new_text` replacements. Every `old_text` must occur
exactly once in the progressively edited content.

Preparation performs path, secret-file, encoding, size, hash and replacement validation.
It produces proposed file bytes, a bounded unified-style preview and an `action_hash`.
The hash covers the normalized paths, expected hashes and hashes of the exact proposed
bytes. `apply_patch` accepts only this action hash; it cannot alter the prepared payload.

In `ask_before_changes`, the runtime records an approval and pauses the run before
execution. Approval or rejection is tied to the action hash. `read_only` denies both
patch tools. `auto_edit_workspace` may apply this low-risk existing-file replacement
without approval, but all hard validation remains active.

Immediately before writing, every target is resolved again and its content hash must
still match the prepared snapshot. All proposed files are staged beside their targets
before any target changes. The runtime retains same-directory backups while replacing
the batch and restores every already-replaced target if a later replacement fails.
Backups and staged files are removed after success or successful rollback. If rollback
itself fails, remaining backups are retained for manual recovery. A rollback failure is a
hard apply failure and is surfaced for diagnostics; the runtime never reports success for
a partial batch.

This first edit slice changes existing regular UTF-8 files only. File creation, delete,
rename, binary edits and secret-like paths remain unsupported and require separate
policy decisions.

## Consequences

- Patch behavior is deterministic and straightforward to validate.
- Approval always describes the exact proposed bytes shown in the UI.
- Concurrent user edits fail as stale instead of being overwritten.
- Exact replacements can be verbose for large rewrites; later formats may be added as
  separately versioned tools without weakening this contract.
- Batch replacement is atomic from the application's reported outcome, with compensating
  rollback across files; the filesystem cannot provide a single native transaction over
  several independent files.
