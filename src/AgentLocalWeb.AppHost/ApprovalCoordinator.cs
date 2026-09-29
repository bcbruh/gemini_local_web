using System.Collections.Concurrent;
using AgentLocalWeb.Agent.Core;
using AgentLocalWeb.Persistence;
using AgentLocalWeb.Tools;

namespace AgentLocalWeb.AppHost;

internal sealed class ApprovalCoordinator(SqliteAppStore store) : IToolApprovalGateway
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<string, PendingDecision> _pending =
        new(StringComparer.Ordinal);

    public async ValueTask<ToolApprovalDecision> RequestAsync(
        ToolApprovalRequest request,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        PersistedApproval approval;
        var completion = new TaskCompletionSource<ToolApprovalDecision>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            approval = await store.CreateApprovalAsync(
                request.RunId,
                request.RequestId,
                request.Tool,
                request.Details.ActionHash,
                request.Details.Title,
                request.Details.Diff,
                cancellationToken).ConfigureAwait(false);
            if (!_pending.TryAdd(
                    approval.Id,
                    new PendingDecision(approval.ActionHash, completion)))
            {
                throw new InvalidOperationException("The approval is already pending.");
            }
        }
        finally
        {
            _gate.Release();
        }

        try
        {
            return await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _pending.TryRemove(approval.Id, out _);
            if (!completion.Task.IsCompleted)
            {
                await store.ExpireApprovalAsync(approval.Id, CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }
    }

    public async Task<PersistedApproval> DecideAsync(
        string approvalId,
        string actionHash,
        ToolApprovalDecision decision,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_pending.TryGetValue(approvalId, out var pending) ||
                !pending.ActionHash.Equals(actionHash, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "The approval is no longer active or the action hash does not match.");
            }

            var persisted = await store.DecideApprovalAsync(
                approvalId,
                actionHash,
                decision == ToolApprovalDecision.Approved ? "approved" : "rejected",
                cancellationToken).ConfigureAwait(false);
            pending.Completion.TrySetResult(decision);
            return persisted;
        }
        finally
        {
            _gate.Release();
        }
    }

    private sealed record PendingDecision(
        string ActionHash,
        TaskCompletionSource<ToolApprovalDecision> Completion);
}

internal sealed class SqliteWorkspacePermissionModeProvider(SqliteAppStore store)
    : IWorkspacePermissionModeProvider
{
    public async ValueTask<WorkspacePermissionMode> GetAsync(
        CancellationToken cancellationToken = default)
    {
        var state = await store.GetStateAsync(cancellationToken).ConfigureAwait(false);
        return state.PermissionMode switch
        {
            "read_only" => WorkspacePermissionMode.ReadOnly,
            "ask_before_changes" => WorkspacePermissionMode.AskBeforeChanges,
            "auto_edit_workspace" => WorkspacePermissionMode.AutoEditWorkspace,
            _ => throw new InvalidOperationException("The stored permission mode is invalid.")
        };
    }
}
