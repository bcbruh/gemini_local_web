using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using AgentLocalWeb.Agent.Core;
using AgentLocalWeb.Tools;
using AgentLocalWeb.Workspace;

namespace AgentLocalWeb.Tools.Tests;

public sealed class ReadOnlyWorkspaceToolsTests
{
    [Fact]
    public async Task ExecutesAllFourReadOnlyToolsWithBoundedJsonResults()
    {
        using var fixture = new ToolFixture();
        fixture.Write("src/app.cs", "first\nfind me\nthird");
        var tools = fixture.CreateTools();
        var cancellationToken = TestContext.Current.CancellationToken;

        var list = await tools.ExecuteAsync(
            Request("list-1", "list_directory", """{"path":"src"}"""),
            cancellationToken);
        var read = await tools.ExecuteAsync(
            Request(
                "read-1",
                "read_file",
                """{"path":"src/app.cs","start_line":2,"end_line":2}"""),
            cancellationToken);
        var files = await tools.ExecuteAsync(
            Request("files-1", "search_files", """{"pattern":"*.cs"}"""),
            cancellationToken);
        var text = await tools.ExecuteAsync(
            Request(
                "text-1",
                "search_text",
                """{"query":"find me","file_pattern":"*.cs"}"""),
            cancellationToken);

        Assert.All([list, read, files, text], result => Assert.True(result.Succeeded));
        Assert.Equal("find me", read.Output?.GetProperty("content").GetString());
        Assert.Single(files.Output?.GetProperty("matches").EnumerateArray() ?? []);
        Assert.Single(text.Output?.GetProperty("matches").EnumerateArray() ?? []);
    }

    [Theory]
    [InlineData("{\"path\":\"src/app.cs\",\"unknown\":true}")]
    [InlineData("{\"start_line\":1}")]
    [InlineData("{\"path\":42}")]
    public async Task StrictlyRejectsInvalidReadArguments(string arguments)
    {
        using var fixture = new ToolFixture();
        fixture.Write("src/app.cs", "content");

        var result = await fixture.CreateTools().ExecuteAsync(
            Request("read-1", "read_file", arguments),
            TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(ToolExecutionErrorCode.InvalidArguments, result.Error?.Code);
        Assert.Null(result.Output);
    }

    [Fact]
    public async Task PreservesRequestIdAndBlocksSecretContent()
    {
        using var fixture = new ToolFixture();
        fixture.Write(".env", "TOKEN=secret");

        var result = await fixture.CreateTools().ExecuteAsync(
            Request("secret-call", "read_file", """{"path":".env"}"""),
            TestContext.Current.CancellationToken);

        Assert.Equal("secret-call", result.RequestId);
        Assert.Equal(ToolExecutionErrorCode.ContentBlocked, result.Error?.Code);
    }

    [Fact]
    public async Task RejectsUnsupportedToolAtDispatcherBoundary()
    {
        using var fixture = new ToolFixture();

        var result = await fixture.CreateTools().ExecuteAsync(
            Request("call-1", "run_command", "{}"),
            TestContext.Current.CancellationToken);

        Assert.Equal(ToolExecutionErrorCode.UnsupportedTool, result.Error?.Code);
    }

    [Fact]
    public async Task AskModePreparesDiffAndRequiresExactApplyApproval()
    {
        using var fixture = new ToolFixture();
        fixture.Write("file.txt", "old value\n");
        var tools = fixture.CreateDispatcher(WorkspacePermissionMode.AskBeforeChanges);
        var cancellationToken = TestContext.Current.CancellationToken;
        var prepare = Request(
            "prepare-1",
            "prepare_patch",
            JsonSerializer.Serialize(new
            {
                files = new[]
                {
                    new
                    {
                        path = "file.txt",
                        expected_sha256 = fixture.Hash("file.txt"),
                        replacements = new[] { new { old_text = "old value", new_text = "new value" } }
                    }
                }
            }));

        Assert.Null((await tools.PlanAsync(prepare, cancellationToken)).Rejection);
        var prepared = await tools.ExecuteAsync(prepare, cancellationToken);
        var actionHash = prepared.Output?.GetProperty("action_hash").GetString();
        var apply = Request(
            "apply-1",
            "apply_patch",
            JsonSerializer.Serialize(new { action_hash = actionHash }));
        var plan = await tools.PlanAsync(apply, cancellationToken);

        Assert.True(prepared.Succeeded);
        Assert.Equal(actionHash, plan.Approval?.ActionHash);
        Assert.Contains("-old value", plan.Approval?.Diff, StringComparison.Ordinal);
        Assert.Equal("old value\n", fixture.Read("file.txt"));
    }

    [Fact]
    public async Task ReadOnlyDeniesPatchAndAutoEditStillAppliesValidatedPatch()
    {
        using var fixture = new ToolFixture();
        fixture.Write("file.txt", "old");
        var cancellationToken = TestContext.Current.CancellationToken;
        var prepare = Request(
            "prepare-1",
            "prepare_patch",
            JsonSerializer.Serialize(new
            {
                files = new[]
                {
                    new
                    {
                        path = "file.txt",
                        expected_sha256 = fixture.Hash("file.txt"),
                        replacements = new[] { new { old_text = "old", new_text = "new" } }
                    }
                }
            }));
        var readOnly = fixture.CreateDispatcher(WorkspacePermissionMode.ReadOnly);
        Assert.Equal(ToolExecutionErrorCode.AccessDenied,
            (await readOnly.PlanAsync(prepare, cancellationToken)).Rejection?.Error?.Code);

        var auto = fixture.CreateDispatcher(WorkspacePermissionMode.AutoEditWorkspace);
        var prepared = await auto.ExecuteAsync(prepare, cancellationToken);
        var apply = Request(
            "apply-1",
            "apply_patch",
            JsonSerializer.Serialize(new
            {
                action_hash = prepared.Output?.GetProperty("action_hash").GetString()
            }));
        var plan = await auto.PlanAsync(apply, cancellationToken);
        var applied = await auto.ExecuteAsync(apply, cancellationToken);

        Assert.Null(plan.Approval);
        Assert.True(applied.Succeeded);
        Assert.Equal("new", fixture.Read("file.txt"));
    }

    private static ProtocolResponse.ToolRequest Request(
        string requestId,
        string tool,
        string arguments)
    {
        using var document = JsonDocument.Parse(arguments);
        return new ProtocolResponse.ToolRequest(requestId, tool, document.RootElement.Clone());
    }

    private sealed class ToolFixture : IDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(),
            "AgentLocalWeb.ToolTests",
            Guid.NewGuid().ToString("N"));

        public ToolFixture()
        {
            Directory.CreateDirectory(_root);
        }

        public ReadOnlyWorkspaceTools CreateTools() =>
            new(new WorkspaceReader(new WorkspaceManager().Select(_root)));

        public WorkspaceToolDispatcher CreateDispatcher(WorkspacePermissionMode mode)
        {
            var manager = new WorkspaceManager();
            manager.Select(_root);
            return new WorkspaceToolDispatcher(manager, new FixedPermissionModeProvider(mode));
        }

        public void Write(string relativePath, string content)
        {
            var path = Path.Combine(_root, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }

        public string Read(string relativePath) => File.ReadAllText(Path.Combine(_root, relativePath));

        public string Hash(string relativePath) => Convert.ToHexString(
            SHA256.HashData(File.ReadAllBytes(Path.Combine(_root, relativePath))))
            .ToLowerInvariant();

        public void Dispose()
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
    }

    private sealed class FixedPermissionModeProvider(WorkspacePermissionMode mode)
        : IWorkspacePermissionModeProvider
    {
        public ValueTask<WorkspacePermissionMode> GetAsync(
            CancellationToken cancellationToken = default) => ValueTask.FromResult(mode);
    }
}
