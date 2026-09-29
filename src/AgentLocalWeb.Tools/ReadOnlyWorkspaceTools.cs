using System.Text.Json;
using System.Text.Json.Serialization;
using AgentLocalWeb.Agent.Core;
using AgentLocalWeb.Workspace;

namespace AgentLocalWeb.Tools;

public sealed class ReadOnlyWorkspaceTools(WorkspaceReader reader) : IToolDispatcher
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

    private static readonly IReadOnlySet<string> ToolNames = new HashSet<string>(
        ["list_directory", "read_file", "search_files", "search_text"],
        StringComparer.Ordinal);

    public IReadOnlySet<string> SupportedTools => ToolNames;

    public async ValueTask<ToolExecutionResult> ExecuteAsync(
        ProtocolResponse.ToolRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!ToolNames.Contains(request.Tool))
        {
            return Failure(
                request,
                ToolExecutionErrorCode.UnsupportedTool,
                "The requested tool is not available.");
        }

        try
        {
            var output = request.Tool switch
            {
                "list_directory" => ExecuteList(request.Arguments),
                "read_file" => await ExecuteReadAsync(request.Arguments, cancellationToken)
                    .ConfigureAwait(false),
                "search_files" => ExecuteSearchFiles(request.Arguments, cancellationToken),
                "search_text" => await ExecuteSearchTextAsync(
                    request.Arguments,
                    cancellationToken).ConfigureAwait(false),
                _ => throw new InvalidOperationException("The tool registry is inconsistent.")
            };
            return ToolExecutionResult.Success(request.RequestId, request.Tool, output);
        }
        catch (JsonException)
        {
            return Failure(
                request,
                ToolExecutionErrorCode.InvalidArguments,
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
        catch (WorkspaceReadException exception)
        {
            return Failure(
                request,
                exception.Error is WorkspaceReadError.BinaryFile or
                    WorkspaceReadError.FileTooLarge or
                    WorkspaceReadError.SuspectedSecret
                    ? ToolExecutionErrorCode.ContentBlocked
                    : ToolExecutionErrorCode.InvalidArguments,
                exception.Message);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Failure(
                request,
                ToolExecutionErrorCode.Cancelled,
                "The tool call was cancelled.");
        }
        catch (IOException)
        {
            return Failure(
                request,
                ToolExecutionErrorCode.ExecutionFailed,
                "The workspace could not be read.");
        }
    }

    private static ToolExecutionResult Failure(
        ProtocolResponse.ToolRequest request,
        ToolExecutionErrorCode code,
        string message) =>
        ToolExecutionResult.Failure(request.RequestId, request.Tool, code, message);

    private JsonElement ExecuteList(JsonElement arguments)
    {
        var input = Deserialize<ListDirectoryInput>(arguments);
        return JsonSerializer.SerializeToElement(
            reader.ListDirectory(input.Path, input.MaximumEntries),
            OutputOptions);
    }

    private async Task<JsonElement> ExecuteReadAsync(
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        var input = Deserialize<ReadFileInput>(arguments);
        var result = await reader.ReadFileAsync(
            input.Path,
            input.StartLine,
            input.EndLine,
            cancellationToken).ConfigureAwait(false);
        return JsonSerializer.SerializeToElement(result, OutputOptions);
    }

    private JsonElement ExecuteSearchFiles(
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        var input = Deserialize<SearchFilesInput>(arguments);
        return JsonSerializer.SerializeToElement(
            reader.SearchFiles(input.Pattern, input.MaximumResults, cancellationToken),
            OutputOptions);
    }

    private async Task<JsonElement> ExecuteSearchTextAsync(
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        var input = Deserialize<SearchTextInput>(arguments);
        var result = await reader.SearchTextAsync(
            input.Query,
            input.FilePattern,
            input.CaseSensitive,
            input.MaximumResults,
            cancellationToken).ConfigureAwait(false);
        return JsonSerializer.SerializeToElement(result, OutputOptions);
    }

    private static T Deserialize<T>(JsonElement arguments) where T : class =>
        arguments.Deserialize<T>(InputOptions)
        ?? throw new JsonException("Tool arguments are required.");

    private sealed record ListDirectoryInput
    {
        [JsonPropertyName("path")]
        public string Path { get; init; } = string.Empty;

        [JsonPropertyName("maximum_entries")]
        public int MaximumEntries { get; init; } = 100;
    }

    private sealed record ReadFileInput
    {
        [JsonPropertyName("path")]
        public required string Path { get; init; }

        [JsonPropertyName("start_line")]
        public int StartLine { get; init; } = 1;

        [JsonPropertyName("end_line")]
        public int? EndLine { get; init; }
    }

    private sealed record SearchFilesInput
    {
        [JsonPropertyName("pattern")]
        public required string Pattern { get; init; }

        [JsonPropertyName("maximum_results")]
        public int MaximumResults { get; init; } = 100;
    }

    private sealed record SearchTextInput
    {
        [JsonPropertyName("query")]
        public required string Query { get; init; }

        [JsonPropertyName("file_pattern")]
        public string FilePattern { get; init; } = "*";

        [JsonPropertyName("case_sensitive")]
        public bool CaseSensitive { get; init; }

        [JsonPropertyName("maximum_results")]
        public int MaximumResults { get; init; } = 100;
    }
}
