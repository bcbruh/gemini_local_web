using System.Text.Json;

namespace AgentLocalWeb.Agent.Core;

public interface IToolDispatcher
{
    IReadOnlySet<string> SupportedTools { get; }

    ValueTask<ToolExecutionResult> ExecuteAsync(
        ProtocolResponse.ToolRequest request,
        CancellationToken cancellationToken = default);
}

public interface IPlannedToolDispatcher : IToolDispatcher
{
    ValueTask<ToolExecutionPlan> PlanAsync(
        ProtocolResponse.ToolRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record ToolExecutionPlan(
    ToolExecutionResult? Rejection = null,
    ToolApprovalDetails? Approval = null)
{
    public static ToolExecutionPlan Immediate { get; } = new();
}

public sealed record ToolApprovalDetails(
    string ActionHash,
    string Title,
    string Diff);

public sealed record ToolApprovalRequest(
    string RunId,
    string RequestId,
    string Tool,
    ToolApprovalDetails Details);

public enum ToolApprovalDecision
{
    Approved,
    Rejected
}

public interface IToolApprovalGateway
{
    ValueTask<ToolApprovalDecision> RequestAsync(
        ToolApprovalRequest request,
        CancellationToken cancellationToken = default);
}

internal sealed class RejectingToolApprovalGateway : IToolApprovalGateway
{
    public static RejectingToolApprovalGateway Instance { get; } = new();

    public ValueTask<ToolApprovalDecision> RequestAsync(
        ToolApprovalRequest request,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(ToolApprovalDecision.Rejected);
}

public sealed record ToolExecutionResult(
    string RequestId,
    string Tool,
    bool Succeeded,
    JsonElement? Output,
    ToolExecutionError? Error)
{
    public static ToolExecutionResult Success(
        string requestId,
        string tool,
        JsonElement output) =>
        new(requestId, tool, true, output, null);

    public static ToolExecutionResult Failure(
        string requestId,
        string tool,
        ToolExecutionErrorCode code,
        string message) =>
        new(requestId, tool, false, null, new ToolExecutionError(code, message));
}

public sealed record ToolExecutionError(
    ToolExecutionErrorCode Code,
    string Message);

public enum ToolExecutionErrorCode
{
    UnsupportedTool,
    InvalidArguments,
    AccessDenied,
    NotFound,
    ContentBlocked,
    StaleFile,
    ApprovalRejected,
    ExecutionFailed,
    Cancelled
}
