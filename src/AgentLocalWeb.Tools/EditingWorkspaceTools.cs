using System.Text.Json;
using System.Text.Json.Serialization;
using AgentLocalWeb.Agent.Core;
using AgentLocalWeb.Workspace;

namespace AgentLocalWeb.Tools;

internal sealed class EditingWorkspaceTools(WorkspacePatchService patches)
{
    private static readonly JsonSerializerOptions InputOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    private static readonly JsonSerializerOptions OutputOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    public async ValueTask<ToolExecutionResult> ExecuteAsync(
        ProtocolResponse.ToolRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var output = request.Tool switch
            {
                "prepare_patch" => await PrepareAsync(request.Arguments, cancellationToken)
                    .ConfigureAwait(false),
                "apply_patch" => await ApplyAsync(request.Arguments, cancellationToken)
                    .ConfigureAwait(false),
                _ => throw new InvalidOperationException("The edit tool registry is inconsistent.")
            };
            return ToolExecutionResult.Success(request.RequestId, request.Tool, output);
        }
        catch (JsonException)
        {
            return Failure(request, ToolExecutionErrorCode.InvalidArguments,
                "The tool arguments do not match the required schema.");
        }
        catch (ArgumentException exception)
        {
            return Failure(request, ToolExecutionErrorCode.InvalidArguments, exception.Message);
        }
        catch (WorkspacePathException exception)
        {
            return Failure(
                request,
                exception.Error == WorkspacePathError.NotFound
                    ? ToolExecutionErrorCode.NotFound
                    : ToolExecutionErrorCode.AccessDenied,
                exception.Message);
        }
        catch (WorkspacePatchException exception)
        {
            return Failure(request, Map(exception.Error), exception.Message);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Failure(request, ToolExecutionErrorCode.Cancelled, "The tool call was cancelled.");
        }
        catch (IOException)
        {
            return Failure(request, ToolExecutionErrorCode.ExecutionFailed,
                "The workspace patch operation failed.");
        }
    }

    public ToolExecutionPlan PlanApply(ProtocolResponse.ToolRequest request)
    {
        try
        {
            var input = Deserialize<ApplyPatchInput>(request.Arguments);
            var patch = patches.GetPrepared(input.ActionHash);
            return new ToolExecutionPlan(Approval: new ToolApprovalDetails(
                patch.ActionHash,
                $"Apply changes to {patch.Paths.Count} file(s)",
                patch.Diff));
        }
        catch (JsonException)
        {
            return Rejected(request, ToolExecutionErrorCode.InvalidArguments,
                "The tool arguments do not match the required schema.");
        }
        catch (WorkspacePatchException exception)
        {
            return Rejected(request, Map(exception.Error), exception.Message);
        }
    }

    private async Task<JsonElement> PrepareAsync(
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        var input = Deserialize<PreparePatchInput>(arguments);
        var files = input.Files.Select(file => new WorkspacePatchFileRequest(
            file.Path,
            file.ExpectedSha256,
            file.Replacements.Select(replacement => new WorkspacePatchReplacement(
                replacement.OldText,
                replacement.NewText)).ToArray())).ToArray();
        var result = await patches.PrepareAsync(files, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.SerializeToElement(new
        {
            result.ActionHash,
            result.Paths,
            result.Diff
        }, OutputOptions);
    }

    private async Task<JsonElement> ApplyAsync(
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        var input = Deserialize<ApplyPatchInput>(arguments);
        var result = await patches.ApplyAsync(input.ActionHash, cancellationToken)
            .ConfigureAwait(false);
        return JsonSerializer.SerializeToElement(new
        {
            result.ActionHash,
            result.Paths
        }, OutputOptions);
    }

    private static T Deserialize<T>(JsonElement arguments) where T : class =>
        arguments.Deserialize<T>(InputOptions)
        ?? throw new JsonException("Tool arguments are required.");

    private static ToolExecutionPlan Rejected(
        ProtocolResponse.ToolRequest request,
        ToolExecutionErrorCode code,
        string message) =>
        new(ToolExecutionResult.Failure(request.RequestId, request.Tool, code, message));

    private static ToolExecutionResult Failure(
        ProtocolResponse.ToolRequest request,
        ToolExecutionErrorCode code,
        string message) =>
        ToolExecutionResult.Failure(request.RequestId, request.Tool, code, message);

    private static ToolExecutionErrorCode Map(WorkspacePatchError error) => error switch
    {
        WorkspacePatchError.InvalidPatch => ToolExecutionErrorCode.InvalidArguments,
        WorkspacePatchError.ContentBlocked => ToolExecutionErrorCode.ContentBlocked,
        WorkspacePatchError.StaleFile => ToolExecutionErrorCode.StaleFile,
        WorkspacePatchError.NotPrepared => ToolExecutionErrorCode.InvalidArguments,
        _ => ToolExecutionErrorCode.ExecutionFailed
    };

    private sealed record PreparePatchInput
    {
        [JsonPropertyName("files")]
        public required IReadOnlyList<PatchFileInput> Files { get; init; }
    }

    private sealed record PatchFileInput
    {
        [JsonPropertyName("path")]
        public required string Path { get; init; }

        [JsonPropertyName("expected_sha256")]
        public required string ExpectedSha256 { get; init; }

        [JsonPropertyName("replacements")]
        public required IReadOnlyList<ReplacementInput> Replacements { get; init; }
    }

    private sealed record ReplacementInput
    {
        [JsonPropertyName("old_text")]
        public required string OldText { get; init; }

        [JsonPropertyName("new_text")]
        public required string NewText { get; init; }
    }

    private sealed record ApplyPatchInput
    {
        [JsonPropertyName("action_hash")]
        public required string ActionHash { get; init; }
    }
}
