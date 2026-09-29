using AgentLocalWeb.Agent.Core;
using AgentLocalWeb.Workspace;

namespace AgentLocalWeb.Tools;

public enum WorkspacePermissionMode
{
    ReadOnly,
    AskBeforeChanges,
    AutoEditWorkspace
}

public interface IWorkspacePermissionModeProvider
{
    ValueTask<WorkspacePermissionMode> GetAsync(CancellationToken cancellationToken = default);
}

public sealed class WorkspaceToolDispatcher(
    WorkspaceManager workspaceManager,
    IWorkspacePermissionModeProvider permissionModeProvider) : IPlannedToolDispatcher
{
    private static readonly IReadOnlySet<string> ToolNames = new HashSet<string>(
        ["list_directory", "read_file", "search_files", "search_text", "prepare_patch", "apply_patch"],
        StringComparer.Ordinal);

    private readonly object _patchLock = new();
    private string? _patchWorkspaceRoot;
    private WorkspacePatchService? _patches;

    public IReadOnlySet<string> SupportedTools => ToolNames;

    public async ValueTask<ToolExecutionPlan> PlanAsync(
        ProtocolResponse.ToolRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Tool is not ("prepare_patch" or "apply_patch"))
        {
            return ToolExecutionPlan.Immediate;
        }

        var workspace = workspaceManager.Current;
        if (workspace is null)
        {
            return Rejected(request, "Select a workspace before using filesystem tools.");
        }

        var mode = await permissionModeProvider.GetAsync(cancellationToken).ConfigureAwait(false);
        if (mode == WorkspacePermissionMode.ReadOnly)
        {
            return Rejected(request, "The current permission mode does not allow workspace edits.");
        }

        if (request.Tool == "prepare_patch" || mode == WorkspacePermissionMode.AutoEditWorkspace)
        {
            return ToolExecutionPlan.Immediate;
        }

        return GetEditingTools(workspace).PlanApply(request);
    }

    public ValueTask<ToolExecutionResult> ExecuteAsync(
        ProtocolResponse.ToolRequest request,
        CancellationToken cancellationToken = default)
    {
        var workspace = workspaceManager.Current;
        if (workspace is null)
        {
            return ValueTask.FromResult(ToolExecutionResult.Failure(
                request.RequestId,
                request.Tool,
                ToolExecutionErrorCode.AccessDenied,
                "Select a workspace before using filesystem tools."));
        }

        if (request.Tool is "prepare_patch" or "apply_patch")
        {
            return GetEditingTools(workspace).ExecuteAsync(request, cancellationToken);
        }

        return new ReadOnlyWorkspaceTools(new WorkspaceReader(workspace))
            .ExecuteAsync(request, cancellationToken);
    }

    private EditingWorkspaceTools GetEditingTools(WorkspaceSelection workspace)
    {
        lock (_patchLock)
        {
            if (_patches is null || !string.Equals(
                    _patchWorkspaceRoot,
                    workspace.CanonicalRoot,
                    OperatingSystem.IsWindows()
                        ? StringComparison.OrdinalIgnoreCase
                        : StringComparison.Ordinal))
            {
                _patches = new WorkspacePatchService(workspace);
                _patchWorkspaceRoot = workspace.CanonicalRoot;
            }

            return new EditingWorkspaceTools(_patches);
        }
    }

    private static ToolExecutionPlan Rejected(
        ProtocolResponse.ToolRequest request,
        string message) =>
        new(ToolExecutionResult.Failure(
            request.RequestId,
            request.Tool,
            ToolExecutionErrorCode.AccessDenied,
            message));
}
