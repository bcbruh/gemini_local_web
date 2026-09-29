using System.Diagnostics;
using AgentLocalWeb.Workspace;

namespace AgentLocalWeb.Workspace.Tests;

public sealed class WorkspaceReaderTests
{
    [Fact]
    public async Task ReadsBoundedLineRangeWithSnapshotMetadata()
    {
        using var fixture = new ReaderFixture();
        fixture.Write("src/app.cs", "one\r\ntwo\r\nthree\r\n");
        var reader = fixture.CreateReader();

        var result = await reader.ReadFileAsync(
            "src/app.cs",
            startLine: 2,
            endLine: 3,
            TestContext.Current.CancellationToken);

        Assert.Equal("two\nthree", result.Content);
        Assert.Equal(2, result.StartLine);
        Assert.Equal(3, result.EndLine);
        Assert.Equal(3, result.TotalLines);
        Assert.Equal(64, result.Sha256.Length);
        Assert.True(result.Size > 0);
    }

    [Fact]
    public async Task RejectsBinaryLargeAndSuspectedSecretFiles()
    {
        using var fixture = new ReaderFixture();
        fixture.WriteBytes("image.bin", [1, 0, 2]);
        fixture.Write("large.txt", new string('x', 33));
        fixture.Write(".env", "TOKEN=secret");
        var reader = fixture.CreateReader(maximumFileBytes: 32);
        var cancellationToken = TestContext.Current.CancellationToken;

        var binary = await Assert.ThrowsAsync<WorkspaceReadException>(
            () => reader.ReadFileAsync("image.bin", cancellationToken: cancellationToken));
        var large = await Assert.ThrowsAsync<WorkspaceReadException>(
            () => reader.ReadFileAsync("large.txt", cancellationToken: cancellationToken));
        var secret = await Assert.ThrowsAsync<WorkspaceReadException>(
            () => reader.ReadFileAsync(".env", cancellationToken: cancellationToken));

        Assert.Equal(WorkspaceReadError.BinaryFile, binary.Error);
        Assert.Equal(WorkspaceReadError.FileTooLarge, large.Error);
        Assert.Equal(WorkspaceReadError.SuspectedSecret, secret.Error);
    }

    [Theory]
    [InlineData(0, null)]
    [InlineData(2, 1)]
    [InlineData(1, 501)]
    public async Task RejectsInvalidOrOversizedLineRanges(int start, int? end)
    {
        using var fixture = new ReaderFixture();
        fixture.Write("file.txt", "content");

        var exception = await Assert.ThrowsAsync<WorkspaceReadException>(
            () => fixture.CreateReader().ReadFileAsync(
                "file.txt",
                start,
                end,
                TestContext.Current.CancellationToken));

        Assert.Equal(WorkspaceReadError.InvalidRange, exception.Error);
    }

    [Fact]
    public void ListsDirectoryWithIgnoreRulesOrderingAndBound()
    {
        using var fixture = new ReaderFixture();
        fixture.Write("z.txt", "z");
        fixture.Write("a.txt", "a");
        fixture.Write("node_modules/package/index.js", "ignored");
        fixture.Write("src/app.cs", "source");

        var result = fixture.CreateReader().ListDirectory(maximumEntries: 2);

        Assert.Equal(["a.txt", "src"], result.Entries.Select(entry => entry.Name));
        Assert.True(result.Truncated);
        Assert.DoesNotContain(result.Entries, entry => entry.Name == "node_modules");
    }

    [Fact]
    public async Task ReadRevalidatesPathAfterWorkspaceLinkChanges()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new ReaderFixture();
        var inside = fixture.CreateDirectory("inside");
        File.WriteAllText(Path.Combine(inside, "file.txt"), "inside");
        var outside = fixture.CreateOutsideDirectory();
        File.WriteAllText(Path.Combine(outside, "file.txt"), "outside");
        var alias = Path.Combine(fixture.Workspace, "alias");
        Assert.True(CreateDirectoryJunction(alias, inside));
        var reader = fixture.CreateReader();

        await reader.ReadFileAsync(
            "alias/file.txt",
            cancellationToken: TestContext.Current.CancellationToken);
        Directory.Delete(alias);
        Assert.True(CreateDirectoryJunction(alias, outside));

        try
        {
            var exception = await Assert.ThrowsAsync<WorkspacePathException>(
                () => reader.ReadFileAsync(
                    "alias/file.txt",
                    cancellationToken: TestContext.Current.CancellationToken));
            Assert.Equal(WorkspacePathError.OutsideWorkspace, exception.Error);
        }
        finally
        {
            if (Directory.Exists(alias))
            {
                Directory.Delete(alias);
            }
        }
    }

    [Fact]
    public void SearchesFilesRecursivelyWithGlobIgnoreRulesAndBound()
    {
        using var fixture = new ReaderFixture();
        fixture.Write("src/Zeta.cs", "class Zeta {}");
        fixture.Write("src/Alpha.cs", "class Alpha {}");
        fixture.Write("src/readme.md", "docs");
        fixture.Write("node_modules/ignored.cs", "ignored");

        var result = fixture.CreateReader().SearchFiles(
            "*.cs",
            maximumResults: 1,
            TestContext.Current.CancellationToken);

        Assert.Single(result.Matches);
        Assert.Equal(Path.Combine("src", "Alpha.cs"), result.Matches[0].Path);
        Assert.True(result.Truncated);
        Assert.DoesNotContain(result.Matches, match => match.Path.Contains("node_modules"));
    }

    [Fact]
    public async Task SearchesTextWithBoundedPreviewAndSkipsUnsafeFiles()
    {
        using var fixture = new ReaderFixture();
        fixture.Write("src/one.cs", "first\nNeedle " + new string('x', 400));
        fixture.Write("src/two.cs", "needle twice\nneedle again");
        fixture.Write(".env", "needle secret");
        fixture.WriteBytes("binary.cs", [0, 1, 2]);

        var result = await fixture.CreateReader().SearchTextAsync(
            "needle",
            "*.cs",
            maximumResults: 2,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, result.Matches.Count);
        Assert.True(result.Truncated);
        Assert.All(result.Matches, match => Assert.DoesNotContain(".env", match.Path));
        Assert.All(result.Matches, match => Assert.True(match.Preview.Length <= 301));
    }

    [Fact]
    public void SearchRejectsTraversalPattern()
    {
        using var fixture = new ReaderFixture();

        var exception = Assert.Throws<WorkspaceReadException>(
            () => fixture.CreateReader().SearchFiles(
                "../*.cs",
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(WorkspaceReadError.InvalidSearch, exception.Error);
    }

    private static bool CreateDirectoryJunction(string link, string target)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            ArgumentList = { "/c", "mklink", "/J", link, target }
        });
        process?.WaitForExit();
        return process?.ExitCode == 0 && Directory.Exists(link);
    }

    private sealed class ReaderFixture : IDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(),
            "AgentLocalWeb.ReaderTests",
            Guid.NewGuid().ToString("N"));

        public ReaderFixture()
        {
            Workspace = Path.Combine(_root, "workspace");
            Directory.CreateDirectory(Workspace);
        }

        public string Workspace { get; }

        public WorkspaceReader CreateReader(int maximumFileBytes = 1024 * 1024) =>
            new(new WorkspaceManager().Select(Workspace), maximumFileBytes);

        public void Write(string relativePath, string content) =>
            WriteBytes(relativePath, System.Text.Encoding.UTF8.GetBytes(content));

        public void WriteBytes(string relativePath, byte[] content)
        {
            var path = Path.Combine(Workspace, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, content);
        }

        public string CreateDirectory(string relativePath)
        {
            var path = Path.Combine(Workspace, relativePath);
            Directory.CreateDirectory(path);
            return path;
        }

        public string CreateOutsideDirectory()
        {
            var path = Path.Combine(_root, "workspace-evil");
            Directory.CreateDirectory(path);
            return path;
        }

        public void Dispose()
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
    }
}
