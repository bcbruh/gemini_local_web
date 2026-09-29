using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace AgentLocalWeb.Persistence;

public sealed class SqliteAppStore : IAsyncDisposable
{
    private const int MaximumEventPayloadBytes = 64 * 1024;
    private const int MaximumMessageBytes = 64 * 1024;
    private readonly string _connectionString;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _writer = new(1, 1);
    private readonly object _signalLock = new();
    private TaskCompletionSource _eventSignal = CreateSignal();
    private long _latestEventSequence;
    private bool _initialized;
    private bool _disposed;

    public SqliteAppStore(string databasePath, TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        DatabasePath = Path.GetFullPath(databasePath);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true
        }.ToString();
    }

    public string DatabasePath { get; }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_initialized)
        {
            return;
        }

        await _writer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized)
            {
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
            await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(connection, null, """
                PRAGMA journal_mode = WAL;
                PRAGMA synchronous = NORMAL;
                """, cancellationToken).ConfigureAwait(false);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);
            await ExecuteAsync(connection, transaction, """
                CREATE TABLE IF NOT EXISTS schema_migrations (
                    version INTEGER PRIMARY KEY,
                    applied_utc TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS app_state (
                    id INTEGER PRIMARY KEY CHECK (id = 1),
                    last_workspace_root TEXT NULL,
                    permission_mode TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS app_events (
                    sequence INTEGER PRIMARY KEY AUTOINCREMENT,
                    occurred_utc TEXT NOT NULL,
                    event_type TEXT NOT NULL,
                    payload_json TEXT NOT NULL CHECK (json_valid(payload_json)),
                    run_id TEXT NULL
                );

                CREATE INDEX IF NOT EXISTS ix_app_events_run_id_sequence
                    ON app_events(run_id, sequence);

                INSERT OR IGNORE INTO app_state(id, last_workspace_root, permission_mode)
                    VALUES (1, NULL, 'ask_before_changes');
                """, cancellationToken).ConfigureAwait(false);

            await using (var migration = connection.CreateCommand())
            {
                migration.Transaction = (SqliteTransaction)transaction;
                migration.CommandText = """
                    INSERT OR IGNORE INTO schema_migrations(version, applied_utc)
                    VALUES (1, $appliedUtc);
                    """;
                migration.Parameters.AddWithValue(
                    "$appliedUtc",
                    _timeProvider.GetUtcNow().ToString("O", CultureInfo.InvariantCulture));
                await migration.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await ApplyConversationMigrationAsync(
                connection,
                (SqliteTransaction)transaction,
                cancellationToken).ConfigureAwait(false);
            await ApplyRunMigrationAsync(
                connection,
                (SqliteTransaction)transaction,
                cancellationToken).ConfigureAwait(false);
            await ApplyToolCallMigrationAsync(
                connection,
                (SqliteTransaction)transaction,
                cancellationToken).ConfigureAwait(false);
            await ApplyApprovalMigrationAsync(
                connection,
                (SqliteTransaction)transaction,
                cancellationToken).ConfigureAwait(false);
            await InterruptUnfinishedRunsAsync(
                connection,
                (SqliteTransaction)transaction,
                cancellationToken).ConfigureAwait(false);

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            await using var latest = connection.CreateCommand();
            latest.CommandText = "SELECT COALESCE(MAX(sequence), 0) FROM app_events;";
            _latestEventSequence = Convert.ToInt64(
                await latest.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                CultureInfo.InvariantCulture);
            _initialized = true;
        }
        finally
        {
            _writer.Release();
        }
    }

    public async Task<PersistedAppState> GetStateAsync(
        CancellationToken cancellationToken = default)
    {
        EnsureReady();
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT last_workspace_root, permission_mode, active_conversation_id
            FROM app_state
            WHERE id = 1;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("The application state row is missing.");
        }

        return new PersistedAppState(
            reader.IsDBNull(0) ? null : reader.GetString(0),
            reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            Volatile.Read(ref _latestEventSequence));
    }

    public async Task SetPermissionModeAsync(
        string permissionMode,
        CancellationToken cancellationToken = default)
    {
        if (permissionMode is not ("read_only" or "ask_before_changes" or "auto_edit_workspace"))
        {
            throw new ArgumentOutOfRangeException(nameof(permissionMode));
        }

        EnsureReady();
        await _writer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = (SqliteTransaction)transaction;
                command.CommandText = "UPDATE app_state SET permission_mode = $mode WHERE id = 1;";
                command.Parameters.AddWithValue("$mode", permissionMode);
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            var appEvent = await InsertEventAsync(
                connection,
                (SqliteTransaction)transaction,
                "settings.permission_changed",
                JsonSerializer.Serialize(new { PermissionMode = permissionMode }),
                null,
                cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            Publish(appEvent.Sequence);
        }
        finally
        {
            _writer.Release();
        }
    }

    public async Task<string> GetOrCreateActiveConversationAsync(
        CancellationToken cancellationToken = default)
    {
        EnsureReady();
        await _writer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);
            await using (var existing = connection.CreateCommand())
            {
                existing.Transaction = (SqliteTransaction)transaction;
                existing.CommandText = """
                    SELECT c.id
                    FROM app_state AS s
                    JOIN conversations AS c ON c.id = s.active_conversation_id
                    WHERE s.id = 1;
                    """;
                var existingId = await existing.ExecuteScalarAsync(cancellationToken)
                    .ConfigureAwait(false) as string;
                if (existingId is not null)
                {
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                    return existingId;
                }
            }

            var conversationId = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
            var now = _timeProvider.GetUtcNow();
            await using (var create = connection.CreateCommand())
            {
                create.Transaction = (SqliteTransaction)transaction;
                create.CommandText = """
                    INSERT INTO conversations(id, title, created_utc, updated_utc)
                    VALUES ($id, 'Main conversation', $createdUtc, $updatedUtc);

                    UPDATE app_state
                    SET active_conversation_id = $id
                    WHERE id = 1;
                    """;
                create.Parameters.AddWithValue("$id", conversationId);
                create.Parameters.AddWithValue(
                    "$createdUtc",
                    now.ToString("O", CultureInfo.InvariantCulture));
                create.Parameters.AddWithValue(
                    "$updatedUtc",
                    now.ToString("O", CultureInfo.InvariantCulture));
                await create.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            var appEvent = await InsertEventAsync(
                connection,
                (SqliteTransaction)transaction,
                "conversation.created",
                JsonSerializer.Serialize(new { ConversationId = conversationId }),
                null,
                cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            Publish(appEvent.Sequence);
            return conversationId;
        }
        finally
        {
            _writer.Release();
        }
    }

    public async Task<PersistedConversation?> GetActiveConversationAsync(
        CancellationToken cancellationToken = default)
    {
        EnsureReady();
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var active = connection.CreateCommand();
        active.CommandText = "SELECT active_conversation_id FROM app_state WHERE id = 1;";
        var conversationId = await active.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)
            as string;
        return conversationId is null
            ? null
            : await ReadConversationAsync(connection, conversationId, cancellationToken)
                .ConfigureAwait(false);
    }

    public async Task<PersistedMessage> AppendConversationMessageAsync(
        string conversationId,
        string role,
        string content,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(role);
        ArgumentException.ThrowIfNullOrWhiteSpace(content);
        if (role is not ("system" or "user" or "assistant" or "tool"))
        {
            throw new ArgumentException("The conversation role is not supported.", nameof(role));
        }

        if (System.Text.Encoding.UTF8.GetByteCount(content) > MaximumMessageBytes)
        {
            throw new ArgumentException("Message content exceeds the 64 KiB limit.", nameof(content));
        }

        EnsureReady();
        await _writer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);
            var messageId = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
            var occurredAt = _timeProvider.GetUtcNow();
            long ordinal;
            await using (var insert = connection.CreateCommand())
            {
                insert.Transaction = (SqliteTransaction)transaction;
                insert.CommandText = """
                    INSERT INTO messages(
                        id, conversation_id, role, content, created_utc, ordinal)
                    SELECT $id, $conversationId, $role, $content, $createdUtc,
                           COALESCE(MAX(ordinal), 0) + 1
                    FROM messages
                    WHERE conversation_id = $conversationId
                    RETURNING ordinal;
                    """;
                insert.Parameters.AddWithValue("$id", messageId);
                insert.Parameters.AddWithValue("$conversationId", conversationId);
                insert.Parameters.AddWithValue("$role", role);
                insert.Parameters.AddWithValue("$content", content);
                insert.Parameters.AddWithValue(
                    "$createdUtc",
                    occurredAt.ToString("O", CultureInfo.InvariantCulture));
                ordinal = Convert.ToInt64(
                    await insert.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                    CultureInfo.InvariantCulture);
            }

            await using (var update = connection.CreateCommand())
            {
                update.Transaction = (SqliteTransaction)transaction;
                update.CommandText = """
                    UPDATE conversations
                    SET updated_utc = $updatedUtc
                    WHERE id = $conversationId;
                    """;
                update.Parameters.AddWithValue("$conversationId", conversationId);
                update.Parameters.AddWithValue(
                    "$updatedUtc",
                    occurredAt.ToString("O", CultureInfo.InvariantCulture));
                if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                {
                    throw new InvalidOperationException("The active conversation no longer exists.");
                }
            }

            var appEvent = await InsertEventAsync(
                connection,
                (SqliteTransaction)transaction,
                "conversation.message_added",
                JsonSerializer.Serialize(new
                {
                    ConversationId = conversationId,
                    MessageId = messageId,
                    Role = role
                }),
                null,
                cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            Publish(appEvent.Sequence);
            return new PersistedMessage(
                messageId,
                conversationId,
                role,
                content,
                occurredAt,
                ordinal);
        }
        finally
        {
            _writer.Release();
        }
    }

    public async Task<PersistedAppEvent> SaveWorkspaceSelectionAsync(
        string canonicalRoot,
        string payloadJson,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalRoot);
        ValidateEvent("workspace.selected", payloadJson, null);
        EnsureReady();

        await _writer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);

            await using (var state = connection.CreateCommand())
            {
                state.Transaction = (SqliteTransaction)transaction;
                state.CommandText = """
                    UPDATE app_state
                    SET last_workspace_root = $root
                    WHERE id = 1;
                    """;
                state.Parameters.AddWithValue("$root", canonicalRoot);
                await state.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            var appEvent = await InsertEventAsync(
                connection,
                (SqliteTransaction)transaction,
                "workspace.selected",
                payloadJson,
                null,
                cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            Publish(appEvent.Sequence);
            return appEvent;
        }
        finally
        {
            _writer.Release();
        }
    }

    public async Task<PersistedRun> CreateRunAsync(
        string runId,
        string conversationId,
        string protocolVersion,
        string promptVersion,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentifier(runId, nameof(runId));
        ValidateIdentifier(conversationId, nameof(conversationId));
        ValidateIdentifier(protocolVersion, nameof(protocolVersion));
        ValidateIdentifier(promptVersion, nameof(promptVersion));
        EnsureReady();
        await _writer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);
            var now = _timeProvider.GetUtcNow();
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = (SqliteTransaction)transaction;
                command.CommandText = """
                    INSERT INTO runs(
                        id, conversation_id, status, protocol_version, prompt_version,
                        created_utc, updated_utc, completed_utc, error_category, cancel_requested)
                    VALUES (
                        $id, $conversationId, 'queued', $protocolVersion, $promptVersion,
                        $createdUtc, $updatedUtc, NULL, NULL, 0);
                    """;
                command.Parameters.AddWithValue("$id", runId);
                command.Parameters.AddWithValue("$conversationId", conversationId);
                command.Parameters.AddWithValue("$protocolVersion", protocolVersion);
                command.Parameters.AddWithValue("$promptVersion", promptVersion);
                command.Parameters.AddWithValue("$createdUtc", now.ToString("O", CultureInfo.InvariantCulture));
                command.Parameters.AddWithValue("$updatedUtc", now.ToString("O", CultureInfo.InvariantCulture));
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            var appEvent = await InsertEventAsync(
                connection,
                (SqliteTransaction)transaction,
                "run.created",
                JsonSerializer.Serialize(new
                {
                    RunId = runId,
                    ConversationId = conversationId,
                    Status = "queued"
                }),
                runId,
                cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            Publish(appEvent.Sequence);
            return new PersistedRun(
                runId,
                conversationId,
                "queued",
                protocolVersion,
                promptVersion,
                now,
                now,
                null,
                null,
                false);
        }
        finally
        {
            _writer.Release();
        }
    }

    public async Task<PersistedRun> TransitionRunAsync(
        string runId,
        string expectedStatus,
        string nextStatus,
        string? errorCategory = null,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentifier(runId, nameof(runId));
        ValidateIdentifier(expectedStatus, nameof(expectedStatus));
        ValidateIdentifier(nextStatus, nameof(nextStatus));
        if (errorCategory is not null)
        {
            ValidateIdentifier(errorCategory, nameof(errorCategory));
        }

        EnsureReady();
        await _writer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);
            var now = _timeProvider.GetUtcNow();
            var terminal = nextStatus is "completed" or "failed" or "cancelled" or "interrupted";
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = (SqliteTransaction)transaction;
                command.CommandText = """
                    UPDATE runs
                    SET status = $nextStatus,
                        updated_utc = $updatedUtc,
                        completed_utc = CASE WHEN $terminal = 1 THEN $updatedUtc ELSE NULL END,
                        error_category = $errorCategory
                    WHERE id = $id AND status = $expectedStatus;
                    """;
                command.Parameters.AddWithValue("$id", runId);
                command.Parameters.AddWithValue("$expectedStatus", expectedStatus);
                command.Parameters.AddWithValue("$nextStatus", nextStatus);
                command.Parameters.AddWithValue("$updatedUtc", now.ToString("O", CultureInfo.InvariantCulture));
                command.Parameters.AddWithValue("$terminal", terminal ? 1 : 0);
                command.Parameters.AddWithValue("$errorCategory", (object?)errorCategory ?? DBNull.Value);
                if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                {
                    throw new InvalidOperationException(
                        $"Run '{runId}' is not in expected state '{expectedStatus}'.");
                }
            }

            var appEvent = await InsertEventAsync(
                connection,
                (SqliteTransaction)transaction,
                "run.transitioned",
                JsonSerializer.Serialize(new
                {
                    RunId = runId,
                    Previous = expectedStatus,
                    Current = nextStatus,
                    ErrorCategory = errorCategory
                }),
                runId,
                cancellationToken).ConfigureAwait(false);
            var persisted = await ReadRunAsync(
                connection,
                runId,
                (SqliteTransaction)transaction,
                cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException("The transitioned run is unavailable.");
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            Publish(appEvent.Sequence);
            return persisted;
        }
        finally
        {
            _writer.Release();
        }
    }

    public async Task<PersistedToolCall> CreateToolCallAsync(
        string runId,
        string requestId,
        string tool,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentifier(runId, nameof(runId));
        ValidateIdentifier(requestId, nameof(requestId));
        ValidateIdentifier(tool, nameof(tool));
        EnsureReady();
        await _writer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);
            var now = _timeProvider.GetUtcNow();
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = (SqliteTransaction)transaction;
                command.CommandText = """
                    INSERT INTO tool_calls(
                        run_id, request_id, tool, status, created_utc, updated_utc,
                        completed_utc, error_code)
                    VALUES ($runId, $requestId, $tool, 'requested', $now, $now, NULL, NULL);
                    """;
                command.Parameters.AddWithValue("$runId", runId);
                command.Parameters.AddWithValue("$requestId", requestId);
                command.Parameters.AddWithValue("$tool", tool);
                command.Parameters.AddWithValue("$now", now.ToString("O", CultureInfo.InvariantCulture));
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            var appEvent = await InsertEventAsync(
                connection,
                (SqliteTransaction)transaction,
                "tool.requested",
                JsonSerializer.Serialize(new { RunId = runId, Tool = tool, Status = "requested" }),
                runId,
                cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            Publish(appEvent.Sequence);
            return new PersistedToolCall(runId, requestId, tool, "requested", now, now, null, null);
        }
        finally
        {
            _writer.Release();
        }
    }

    public async Task<PersistedToolCall> FinishToolCallAsync(
        string runId,
        string requestId,
        string status,
        string? errorCode = null,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentifier(runId, nameof(runId));
        ValidateIdentifier(requestId, nameof(requestId));
        if (status is not ("succeeded" or "failed" or "cancelled"))
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }

        if (errorCode is not null)
        {
            ValidateIdentifier(errorCode, nameof(errorCode));
        }

        EnsureReady();
        await _writer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);
            var now = _timeProvider.GetUtcNow();
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = (SqliteTransaction)transaction;
                command.CommandText = """
                    UPDATE tool_calls
                    SET status = $status, updated_utc = $now, completed_utc = $now,
                        error_code = $errorCode
                    WHERE run_id = $runId AND request_id = $requestId AND status = 'requested';
                    """;
                command.Parameters.AddWithValue("$runId", runId);
                command.Parameters.AddWithValue("$requestId", requestId);
                command.Parameters.AddWithValue("$status", status);
                command.Parameters.AddWithValue("$now", now.ToString("O", CultureInfo.InvariantCulture));
                command.Parameters.AddWithValue("$errorCode", (object?)errorCode ?? DBNull.Value);
                if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                {
                    throw new InvalidOperationException(
                        "The tool call was not pending or did not exist.");
                }
            }

            var persisted = await ReadToolCallAsync(
                connection,
                (SqliteTransaction)transaction,
                runId,
                requestId,
                cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The finished tool call is unavailable.");
            var appEvent = await InsertEventAsync(
                connection,
                (SqliteTransaction)transaction,
                "tool.finished",
                JsonSerializer.Serialize(new
                {
                    RunId = runId,
                    persisted.Tool,
                    Status = status,
                    ErrorCode = errorCode
                }),
                runId,
                cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            Publish(appEvent.Sequence);
            return persisted;
        }
        finally
        {
            _writer.Release();
        }
    }

    public async Task<IReadOnlyList<PersistedToolCall>> ReadToolCallsForRunAsync(
        string runId,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentifier(runId, nameof(runId));
        EnsureReady();
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT run_id, request_id, tool, status, created_utc, updated_utc,
                   completed_utc, error_code
            FROM tool_calls
            WHERE run_id = $runId
            ORDER BY created_utc, rowid;
            """;
        command.Parameters.AddWithValue("$runId", runId);
        var calls = new List<PersistedToolCall>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            calls.Add(MapToolCall(reader));
        }

        return calls;
    }

    public async Task<PersistedApproval> CreateApprovalAsync(
        string runId,
        string requestId,
        string tool,
        string actionHash,
        string title,
        string diff,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentifier(runId, nameof(runId));
        ValidateIdentifier(requestId, nameof(requestId));
        ValidateIdentifier(tool, nameof(tool));
        ValidateSha256(actionHash, nameof(actionHash));
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        if (Encoding.UTF8.GetByteCount(diff) > 128 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(diff));
        }

        EnsureReady();
        await _writer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);
            var id = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
            var now = _timeProvider.GetUtcNow();
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = (SqliteTransaction)transaction;
                command.CommandText = """
                    INSERT INTO approvals(
                        id, run_id, request_id, tool, action_hash, title, diff, status,
                        created_utc, decided_utc)
                    VALUES ($id, $runId, $requestId, $tool, $actionHash, $title, $diff,
                            'requested', $now, NULL);
                    """;
                command.Parameters.AddWithValue("$id", id);
                command.Parameters.AddWithValue("$runId", runId);
                command.Parameters.AddWithValue("$requestId", requestId);
                command.Parameters.AddWithValue("$tool", tool);
                command.Parameters.AddWithValue("$actionHash", actionHash);
                command.Parameters.AddWithValue("$title", title);
                command.Parameters.AddWithValue("$diff", diff);
                command.Parameters.AddWithValue("$now", now.ToString("O", CultureInfo.InvariantCulture));
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            var appEvent = await InsertEventAsync(
                connection,
                (SqliteTransaction)transaction,
                "approval.requested",
                JsonSerializer.Serialize(new { Id = id, RunId = runId, Tool = tool, ActionHash = actionHash }),
                runId,
                cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            Publish(appEvent.Sequence);
            return new PersistedApproval(
                id, runId, requestId, tool, actionHash, title, diff, "requested", now, null);
        }
        finally
        {
            _writer.Release();
        }
    }

    public async Task<PersistedApproval?> GetPendingApprovalAsync(
        CancellationToken cancellationToken = default)
    {
        EnsureReady();
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, run_id, request_id, tool, action_hash, title, diff, status,
                   created_utc, decided_utc
            FROM approvals
            WHERE status = 'requested'
            ORDER BY created_utc DESC
            LIMIT 1;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? MapApproval(reader)
            : null;
    }

    public async Task<PersistedApproval> DecideApprovalAsync(
        string approvalId,
        string actionHash,
        string decision,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentifier(approvalId, nameof(approvalId));
        ValidateSha256(actionHash, nameof(actionHash));
        if (decision is not ("approved" or "rejected"))
        {
            throw new ArgumentOutOfRangeException(nameof(decision));
        }

        EnsureReady();
        await _writer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);
            var now = _timeProvider.GetUtcNow();
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = (SqliteTransaction)transaction;
                command.CommandText = """
                    UPDATE approvals
                    SET status = $decision, decided_utc = $now
                    WHERE id = $id AND action_hash = $actionHash AND status = 'requested';
                    """;
                command.Parameters.AddWithValue("$id", approvalId);
                command.Parameters.AddWithValue("$actionHash", actionHash);
                command.Parameters.AddWithValue("$decision", decision);
                command.Parameters.AddWithValue("$now", now.ToString("O", CultureInfo.InvariantCulture));
                if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                {
                    throw new InvalidOperationException(
                        "The approval is no longer pending or the action hash does not match.");
                }
            }

            var persisted = await ReadApprovalAsync(
                connection,
                (SqliteTransaction)transaction,
                approvalId,
                cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The decided approval is unavailable.");
            var appEvent = await InsertEventAsync(
                connection,
                (SqliteTransaction)transaction,
                "approval.decided",
                JsonSerializer.Serialize(new
                {
                    Id = approvalId,
                    persisted.RunId,
                    persisted.ActionHash,
                    Decision = decision
                }),
                persisted.RunId,
                cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            Publish(appEvent.Sequence);
            return persisted;
        }
        finally
        {
            _writer.Release();
        }
    }

    public async Task<bool> ExpireApprovalAsync(
        string approvalId,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentifier(approvalId, nameof(approvalId));
        EnsureReady();
        await _writer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);
            var now = _timeProvider.GetUtcNow();
            await using var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = """
                UPDATE approvals
                SET status = 'expired', decided_utc = $now
                WHERE id = $id AND status = 'requested';
                """;
            command.Parameters.AddWithValue("$id", approvalId);
            command.Parameters.AddWithValue("$now", now.ToString("O", CultureInfo.InvariantCulture));
            var changed = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
            PersistedAppEvent? appEvent = null;
            if (changed)
            {
                appEvent = await InsertEventAsync(
                    connection,
                    (SqliteTransaction)transaction,
                    "approval.expired",
                    JsonSerializer.Serialize(new { Id = approvalId }),
                    null,
                    cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            if (appEvent is not null)
            {
                Publish(appEvent.Sequence);
            }

            return changed;
        }
        finally
        {
            _writer.Release();
        }
    }

    public async Task<bool> RequestRunCancellationAsync(
        string runId,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentifier(runId, nameof(runId));
        EnsureReady();
        await _writer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = """
                UPDATE runs
                SET cancel_requested = 1, updated_utc = $updatedUtc
                WHERE id = $id
                  AND status NOT IN ('completed', 'failed', 'cancelled', 'interrupted');
                """;
            command.Parameters.AddWithValue("$id", runId);
            command.Parameters.AddWithValue(
                "$updatedUtc",
                _timeProvider.GetUtcNow().ToString("O", CultureInfo.InvariantCulture));
            var changed = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
            PersistedAppEvent? appEvent = null;
            if (changed)
            {
                appEvent = await InsertEventAsync(
                    connection,
                    (SqliteTransaction)transaction,
                    "run.cancel_requested",
                    JsonSerializer.Serialize(new { RunId = runId }),
                    runId,
                    cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            if (appEvent is not null)
            {
                Publish(appEvent.Sequence);
            }

            return changed;
        }
        finally
        {
            _writer.Release();
        }
    }

    public async Task<PersistedRun?> GetLatestRunAsync(
        CancellationToken cancellationToken = default)
    {
        EnsureReady();
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, conversation_id, status, protocol_version, prompt_version,
                   created_utc, updated_utc, completed_utc, error_category, cancel_requested
            FROM runs
            ORDER BY created_utc DESC, rowid DESC
            LIMIT 1;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? MapRun(reader)
            : null;
    }

    public async Task ClearWorkspaceAsync(CancellationToken cancellationToken = default)
    {
        EnsureReady();
        await _writer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE app_state SET last_workspace_root = NULL WHERE id = 1;";
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writer.Release();
        }
    }

    public async Task<PersistedAppEvent> AppendEventAsync(
        string type,
        string payloadJson,
        string? runId = null,
        CancellationToken cancellationToken = default)
    {
        ValidateEvent(type, payloadJson, runId);
        EnsureReady();
        await _writer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);
            var appEvent = await InsertEventAsync(
                connection,
                (SqliteTransaction)transaction,
                type,
                payloadJson,
                runId,
                cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            Publish(appEvent.Sequence);
            return appEvent;
        }
        finally
        {
            _writer.Release();
        }
    }

    public async Task<IReadOnlyList<PersistedAppEvent>> ReadEventsAfterAsync(
        long sequence,
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        if (sequence < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sequence));
        }

        if (limit is < 1 or > 500)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

        EnsureReady();
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT sequence, occurred_utc, event_type, payload_json, run_id
            FROM app_events
            WHERE sequence > $sequence
            ORDER BY sequence
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$sequence", sequence);
        command.Parameters.AddWithValue("$limit", limit);

        var events = new List<PersistedAppEvent>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            events.Add(new PersistedAppEvent(
                reader.GetInt64(0),
                DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture),
                reader.GetString(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4)));
        }

        return events;
    }

    public Task WaitForEventAfterAsync(long sequence, CancellationToken cancellationToken = default)
    {
        EnsureReady();
        if (Volatile.Read(ref _latestEventSequence) > sequence)
        {
            return Task.CompletedTask;
        }

        lock (_signalLock)
        {
            return Volatile.Read(ref _latestEventSequence) > sequence
                ? Task.CompletedTask
                : _eventSignal.Task.WaitAsync(cancellationToken);
        }
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;
        _writer.Dispose();
        SqliteConnection.ClearAllPools();
        return ValueTask.CompletedTask;
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;";
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task ApplyConversationMigrationAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using (var check = connection.CreateCommand())
        {
            check.Transaction = transaction;
            check.CommandText = "SELECT COUNT(*) FROM schema_migrations WHERE version = 2;";
            var applied = Convert.ToInt32(
                await check.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                CultureInfo.InvariantCulture);
            if (applied != 0)
            {
                return;
            }
        }

        await ExecuteAsync(connection, transaction, """
            ALTER TABLE app_state ADD COLUMN active_conversation_id TEXT NULL;

            CREATE TABLE conversations (
                id TEXT PRIMARY KEY,
                title TEXT NOT NULL,
                created_utc TEXT NOT NULL,
                updated_utc TEXT NOT NULL
            );

            CREATE TABLE messages (
                id TEXT PRIMARY KEY,
                conversation_id TEXT NOT NULL REFERENCES conversations(id) ON DELETE CASCADE,
                role TEXT NOT NULL CHECK (role IN ('system', 'user', 'assistant', 'tool')),
                content TEXT NOT NULL,
                created_utc TEXT NOT NULL,
                ordinal INTEGER NOT NULL,
                UNIQUE(conversation_id, ordinal)
            );

            CREATE INDEX ix_messages_conversation_ordinal
                ON messages(conversation_id, ordinal);
            """, cancellationToken).ConfigureAwait(false);

        await using var record = connection.CreateCommand();
        record.Transaction = transaction;
        record.CommandText = """
            INSERT INTO schema_migrations(version, applied_utc)
            VALUES (2, $appliedUtc);
            """;
        record.Parameters.AddWithValue(
            "$appliedUtc",
            _timeProvider.GetUtcNow().ToString("O", CultureInfo.InvariantCulture));
        await record.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task ApplyRunMigrationAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using (var check = connection.CreateCommand())
        {
            check.Transaction = transaction;
            check.CommandText = "SELECT COUNT(*) FROM schema_migrations WHERE version = 3;";
            var applied = Convert.ToInt32(
                await check.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                CultureInfo.InvariantCulture);
            if (applied != 0)
            {
                return;
            }
        }

        await ExecuteAsync(connection, transaction, """
            CREATE TABLE runs (
                id TEXT PRIMARY KEY,
                conversation_id TEXT NOT NULL REFERENCES conversations(id) ON DELETE CASCADE,
                status TEXT NOT NULL,
                protocol_version TEXT NOT NULL,
                prompt_version TEXT NOT NULL,
                created_utc TEXT NOT NULL,
                updated_utc TEXT NOT NULL,
                completed_utc TEXT NULL,
                error_category TEXT NULL,
                cancel_requested INTEGER NOT NULL DEFAULT 0 CHECK (cancel_requested IN (0, 1))
            );

            CREATE INDEX ix_runs_conversation_created
                ON runs(conversation_id, created_utc DESC);
            CREATE INDEX ix_runs_status
                ON runs(status);
            """, cancellationToken).ConfigureAwait(false);

        await using var record = connection.CreateCommand();
        record.Transaction = transaction;
        record.CommandText = """
            INSERT INTO schema_migrations(version, applied_utc)
            VALUES (3, $appliedUtc);
            """;
        record.Parameters.AddWithValue(
            "$appliedUtc",
            _timeProvider.GetUtcNow().ToString("O", CultureInfo.InvariantCulture));
        await record.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task ApplyToolCallMigrationAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using (var check = connection.CreateCommand())
        {
            check.Transaction = transaction;
            check.CommandText = "SELECT COUNT(*) FROM schema_migrations WHERE version = 4;";
            var applied = Convert.ToInt32(
                await check.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                CultureInfo.InvariantCulture);
            if (applied != 0)
            {
                return;
            }
        }

        await ExecuteAsync(connection, transaction, """
            CREATE TABLE tool_calls (
                run_id TEXT NOT NULL REFERENCES runs(id) ON DELETE CASCADE,
                request_id TEXT NOT NULL,
                tool TEXT NOT NULL,
                status TEXT NOT NULL CHECK (status IN
                    ('requested', 'succeeded', 'failed', 'cancelled', 'interrupted')),
                created_utc TEXT NOT NULL,
                updated_utc TEXT NOT NULL,
                completed_utc TEXT NULL,
                error_code TEXT NULL,
                PRIMARY KEY (run_id, request_id)
            );

            CREATE INDEX ix_tool_calls_run_created
                ON tool_calls(run_id, created_utc);
            """, cancellationToken).ConfigureAwait(false);

        await using var record = connection.CreateCommand();
        record.Transaction = transaction;
        record.CommandText = """
            INSERT INTO schema_migrations(version, applied_utc)
            VALUES (4, $appliedUtc);
            """;
        record.Parameters.AddWithValue(
            "$appliedUtc",
            _timeProvider.GetUtcNow().ToString("O", CultureInfo.InvariantCulture));
        await record.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task ApplyApprovalMigrationAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using (var check = connection.CreateCommand())
        {
            check.Transaction = transaction;
            check.CommandText = "SELECT COUNT(*) FROM schema_migrations WHERE version = 5;";
            var applied = Convert.ToInt32(
                await check.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                CultureInfo.InvariantCulture);
            if (applied != 0)
            {
                return;
            }
        }

        await ExecuteAsync(connection, transaction, """
            CREATE TABLE approvals (
                id TEXT PRIMARY KEY,
                run_id TEXT NOT NULL REFERENCES runs(id) ON DELETE CASCADE,
                request_id TEXT NOT NULL,
                tool TEXT NOT NULL,
                action_hash TEXT NOT NULL,
                title TEXT NOT NULL,
                diff TEXT NOT NULL,
                status TEXT NOT NULL CHECK (status IN
                    ('requested', 'approved', 'rejected', 'expired')),
                created_utc TEXT NOT NULL,
                decided_utc TEXT NULL,
                UNIQUE(run_id, request_id)
            );

            CREATE INDEX ix_approvals_status_created
                ON approvals(status, created_utc DESC);
            """, cancellationToken).ConfigureAwait(false);

        await using var record = connection.CreateCommand();
        record.Transaction = transaction;
        record.CommandText = """
            INSERT INTO schema_migrations(version, applied_utc)
            VALUES (5, $appliedUtc);
            """;
        record.Parameters.AddWithValue(
            "$appliedUtc",
            _timeProvider.GetUtcNow().ToString("O", CultureInfo.InvariantCulture));
        await record.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task InterruptUnfinishedRunsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        var runIds = new List<string>();
        await using (var query = connection.CreateCommand())
        {
            query.Transaction = transaction;
            query.CommandText = """
                SELECT id
                FROM runs
                WHERE status NOT IN ('completed', 'failed', 'cancelled', 'interrupted');
                """;
            await using var reader = await query.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                runIds.Add(reader.GetString(0));
            }
        }

        foreach (var runId in runIds)
        {
            var now = _timeProvider.GetUtcNow();
            var unfinishedTools = new List<string>();
            await using (var toolQuery = connection.CreateCommand())
            {
                toolQuery.Transaction = transaction;
                toolQuery.CommandText = """
                    SELECT request_id FROM tool_calls
                    WHERE run_id = $runId AND status = 'requested';
                    """;
                toolQuery.Parameters.AddWithValue("$runId", runId);
                await using var reader = await toolQuery.ExecuteReaderAsync(cancellationToken)
                    .ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    unfinishedTools.Add(reader.GetString(0));
                }
            }

            foreach (var requestId in unfinishedTools)
            {
                await using (var toolUpdate = connection.CreateCommand())
                {
                    toolUpdate.Transaction = transaction;
                    toolUpdate.CommandText = """
                        UPDATE tool_calls
                        SET status = 'interrupted', updated_utc = $now,
                            completed_utc = $now, error_code = 'process_interrupted'
                        WHERE run_id = $runId AND request_id = $requestId;
                        """;
                    toolUpdate.Parameters.AddWithValue("$runId", runId);
                    toolUpdate.Parameters.AddWithValue("$requestId", requestId);
                    toolUpdate.Parameters.AddWithValue("$now", now.ToString("O", CultureInfo.InvariantCulture));
                    await toolUpdate.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }

                await InsertEventAsync(
                    connection,
                    transaction,
                    "tool.interrupted",
                    JsonSerializer.Serialize(new { RunId = runId, Status = "interrupted" }),
                    runId,
                    cancellationToken).ConfigureAwait(false);
            }

            await using (var update = connection.CreateCommand())
            {
                update.Transaction = transaction;
                update.CommandText = """
                    UPDATE runs
                    SET status = 'interrupted', updated_utc = $now, completed_utc = $now,
                        error_category = 'process_interrupted'
                    WHERE id = $id;
                    """;
                update.Parameters.AddWithValue("$id", runId);
                update.Parameters.AddWithValue("$now", now.ToString("O", CultureInfo.InvariantCulture));
                await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await InsertEventAsync(
                connection,
                transaction,
                "run.interrupted",
                JsonSerializer.Serialize(new
                {
                    RunId = runId,
                    Status = "interrupted",
                    ErrorCategory = "process_interrupted"
                }),
                runId,
                cancellationToken).ConfigureAwait(false);
        }

        await using var expireApprovals = connection.CreateCommand();
        expireApprovals.Transaction = transaction;
        expireApprovals.CommandText = """
            UPDATE approvals
            SET status = 'expired', decided_utc = $now
            WHERE status = 'requested';
            """;
        expireApprovals.Parameters.AddWithValue(
            "$now",
            _timeProvider.GetUtcNow().ToString("O", CultureInfo.InvariantCulture));
        await expireApprovals.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<PersistedRun?> ReadRunAsync(
        SqliteConnection connection,
        string runId,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT id, conversation_id, status, protocol_version, prompt_version,
                   created_utc, updated_utc, completed_utc, error_category, cancel_requested
            FROM runs
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", runId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? MapRun(reader)
            : null;
    }

    private static PersistedRun MapRun(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetString(3),
        reader.GetString(4),
        DateTimeOffset.Parse(reader.GetString(5), CultureInfo.InvariantCulture),
        DateTimeOffset.Parse(reader.GetString(6), CultureInfo.InvariantCulture),
        reader.IsDBNull(7)
            ? null
            : DateTimeOffset.Parse(reader.GetString(7), CultureInfo.InvariantCulture),
        reader.IsDBNull(8) ? null : reader.GetString(8),
        reader.GetInt32(9) != 0);

    private static async Task<PersistedToolCall?> ReadToolCallAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string runId,
        string requestId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT run_id, request_id, tool, status, created_utc, updated_utc,
                   completed_utc, error_code
            FROM tool_calls
            WHERE run_id = $runId AND request_id = $requestId;
            """;
        command.Parameters.AddWithValue("$runId", runId);
        command.Parameters.AddWithValue("$requestId", requestId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? MapToolCall(reader)
            : null;
    }

    private static PersistedToolCall MapToolCall(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetString(3),
        DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture),
        DateTimeOffset.Parse(reader.GetString(5), CultureInfo.InvariantCulture),
        reader.IsDBNull(6)
            ? null
            : DateTimeOffset.Parse(reader.GetString(6), CultureInfo.InvariantCulture),
        reader.IsDBNull(7) ? null : reader.GetString(7));

    private static async Task<PersistedApproval?> ReadApprovalAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string approvalId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT id, run_id, request_id, tool, action_hash, title, diff, status,
                   created_utc, decided_utc
            FROM approvals
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", approvalId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? MapApproval(reader)
            : null;
    }

    private static PersistedApproval MapApproval(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetString(3),
        reader.GetString(4),
        reader.GetString(5),
        reader.GetString(6),
        reader.GetString(7),
        DateTimeOffset.Parse(reader.GetString(8), CultureInfo.InvariantCulture),
        reader.IsDBNull(9)
            ? null
            : DateTimeOffset.Parse(reader.GetString(9), CultureInfo.InvariantCulture));

    private static async Task<PersistedConversation?> ReadConversationAsync(
        SqliteConnection connection,
        string conversationId,
        CancellationToken cancellationToken)
    {
        string title;
        DateTimeOffset createdAt;
        DateTimeOffset updatedAt;
        await using (var conversation = connection.CreateCommand())
        {
            conversation.CommandText = """
                SELECT title, created_utc, updated_utc
                FROM conversations
                WHERE id = $id;
                """;
            conversation.Parameters.AddWithValue("$id", conversationId);
            await using var reader = await conversation.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            title = reader.GetString(0);
            createdAt = DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture);
            updatedAt = DateTimeOffset.Parse(reader.GetString(2), CultureInfo.InvariantCulture);
        }

        var messages = new List<PersistedMessage>();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, role, content, created_utc, ordinal
            FROM messages
            WHERE conversation_id = $conversationId
            ORDER BY ordinal;
            """;
        command.Parameters.AddWithValue("$conversationId", conversationId);
        await using var messageReader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await messageReader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            messages.Add(new PersistedMessage(
                messageReader.GetString(0),
                conversationId,
                messageReader.GetString(1),
                messageReader.GetString(2),
                DateTimeOffset.Parse(messageReader.GetString(3), CultureInfo.InvariantCulture),
                messageReader.GetInt64(4)));
        }

        return new PersistedConversation(
            conversationId,
            title,
            createdAt,
            updatedAt,
            messages);
    }

    private async Task<PersistedAppEvent> InsertEventAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string type,
        string payloadJson,
        string? runId,
        CancellationToken cancellationToken)
    {
        var occurredAt = _timeProvider.GetUtcNow();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO app_events(occurred_utc, event_type, payload_json, run_id)
            VALUES ($occurredUtc, $eventType, $payloadJson, $runId)
            RETURNING sequence;
            """;
        command.Parameters.AddWithValue(
            "$occurredUtc",
            occurredAt.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$eventType", type);
        command.Parameters.AddWithValue("$payloadJson", payloadJson);
        command.Parameters.AddWithValue("$runId", (object?)runId ?? DBNull.Value);
        var sequence = Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture);
        return new PersistedAppEvent(sequence, occurredAt, type, payloadJson, runId);
    }

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        System.Data.Common.DbTransaction? transaction,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction?)transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void ValidateEvent(string type, string payloadJson, string? runId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        ArgumentNullException.ThrowIfNull(payloadJson);
        if (type.Length > 100 || runId?.Length > 100)
        {
            throw new ArgumentException("Event identifiers exceed their size limit.");
        }

        if (System.Text.Encoding.UTF8.GetByteCount(payloadJson) > MaximumEventPayloadBytes)
        {
            throw new ArgumentException("Event payload exceeds the 64 KiB limit.", nameof(payloadJson));
        }

        using var document = JsonDocument.Parse(payloadJson);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("Event payload must be a JSON object.", nameof(payloadJson));
        }
    }

    private static void ValidateIdentifier(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Length > 100)
        {
            throw new ArgumentException("Identifier exceeds its size limit.", parameterName);
        }
    }

    private static void ValidateSha256(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length != 64 || !value.All(Uri.IsHexDigit))
        {
            throw new ArgumentException("A SHA-256 value is required.", parameterName);
        }
    }

    private void Publish(long sequence)
    {
        TaskCompletionSource previous;
        lock (_signalLock)
        {
            Volatile.Write(ref _latestEventSequence, sequence);
            previous = _eventSignal;
            _eventSignal = CreateSignal();
        }

        previous.TrySetResult();
    }

    private void EnsureReady()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_initialized)
        {
            throw new InvalidOperationException("InitializeAsync must complete before using the store.");
        }
    }

    private static TaskCompletionSource CreateSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
