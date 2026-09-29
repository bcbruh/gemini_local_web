# Local agent protocol V1

`local-agent/v1` is the strict response contract between a brain response and the local agent runtime. The runtime, not the model, validates and executes tools.

Exactly one JSON object is accepted per brain turn. Markdown fences, surrounding prose, duplicate properties and unknown properties are rejected.

## Final response

```json
{
  "protocol": "local-agent/v1",
  "type": "final",
  "message": "Answer for the user"
}
```

## Tool request

```json
{
  "protocol": "local-agent/v1",
  "type": "tool_request",
  "request_id": "unique-within-the-run",
  "tool": "read_file",
  "arguments": {
    "path": "src/App.cs",
    "start_line": 1,
    "end_line": 200
  }
}
```

V1 permits one tool request per turn. `arguments` must be an object and the requested tool must exist in the runtime registry. A parse or validation failure never creates a tool action; the orchestrator may request a corrected response within its retry budget.

The complete envelope is limited to 64 KiB. Tool-specific schemas impose tighter bounds where appropriate.

## Tool observation

The local runtime, never the brain, creates tool observations. Every observation repeats the exact `request_id` and is sent back using the `TOOL` role.

```json
{
  "protocol": "local-agent/v1",
  "type": "tool_result",
  "request_id": "unique-within-the-run",
  "tool": "read_file",
  "status": "succeeded",
  "output": {}
}
```

Failed calls use `status: "failed"` and a typed, sanitized `error`. Observations are size-bounded before entering model context. Repeated request IDs are rejected and never execute a tool twice.

## Runtime context boundary

The runtime constructs every Brain request from its own versioned system prompt, a bounded
selection of recent local conversation messages, the current user message and messages
created during the active run. Persisted `system` messages are never promoted back into the
trusted system channel.

The default total context budget is 512 KiB with at most 40 historical messages. Older
history is omitted first. The current request and active-run tool observations are mandatory;
if they cannot fit, the run fails with `context.limit` before another Brain request is sent.

## Safe edit tools

`prepare_patch` accepts one to ten existing UTF-8 files. Every file includes the SHA-256
returned by `read_file` and bounded exact replacements:

```json
{
  "protocol": "local-agent/v1",
  "type": "tool_request",
  "request_id": "prepare-1",
  "tool": "prepare_patch",
  "arguments": {
    "files": [{
      "path": "src/App.cs",
      "expected_sha256": "64 lowercase or uppercase hex characters",
      "replacements": [{
        "old_text": "exact text occurring once",
        "new_text": "replacement text"
      }]
    }]
  }
}
```

Preparation does not write the workspace. Its observation contains a bounded diff preview,
the normalized target paths and an `action_hash` over the exact proposed bytes. The model
can then request `apply_patch` with only that hash:

```json
{
  "protocol": "local-agent/v1",
  "type": "tool_request",
  "request_id": "apply-1",
  "tool": "apply_patch",
  "arguments": { "action_hash": "64 hex characters" }
}
```

In `ask_before_changes`, the run pauses in `waiting_for_approval` and the local UI displays
the stored diff. Approval is valid only for the matching action hash. A rejection is returned
to the brain as a failed tool observation. Immediately before apply, every file hash and
resolved physical target are checked again; stale files fail without being overwritten.
`read_only` denies both edit tools, while `auto_edit_workspace` skips interactive approval
but retains all path, hash, encoding, secret-file, size and rollback checks.
