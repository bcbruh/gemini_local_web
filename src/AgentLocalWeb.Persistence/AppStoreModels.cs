namespace AgentLocalWeb.Persistence;

public sealed record PersistedAppState(
    string? LastWorkspaceRoot,
    string PermissionMode,
    string? ActiveConversationId,
    long LatestEventSequence);

public sealed record PersistedAppEvent(
    long Sequence,
    DateTimeOffset OccurredAt,
    string Type,
    string PayloadJson,
    string? RunId);

public sealed record PersistedConversation(
    string Id,
    string Title,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<PersistedMessage> Messages);

public sealed record PersistedMessage(
    string Id,
    string ConversationId,
    string Role,
    string Content,
    DateTimeOffset CreatedAt,
    long Ordinal);

public sealed record PersistedRun(
    string Id,
    string ConversationId,
    string Status,
    string ProtocolVersion,
    string PromptVersion,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? CompletedAt,
    string? ErrorCategory,
    bool CancelRequested);

public sealed record PersistedToolCall(
    string RunId,
    string RequestId,
    string Tool,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? CompletedAt,
    string? ErrorCode);

public sealed record PersistedApproval(
    string Id,
    string RunId,
    string RequestId,
    string Tool,
    string ActionHash,
    string Title,
    string Diff,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? DecidedAt);
