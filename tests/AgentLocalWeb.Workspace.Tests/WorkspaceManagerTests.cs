using AgentLocalWeb.Workspace;

namespace AgentLocalWeb.Workspace.Tests;

public sealed class WorkspaceManagerTests
{
    [Fact]
    public void SelectCanonicalizesExistingDirectory()
    {
        using var directory = new TemporaryDirectory("workspace with spaces");
        var manager = new WorkspaceManager();

        var selected = manager.Select(directory.Path + Path.DirectorySeparatorChar);

        Assert.Equal(Path.TrimEndingDirectorySeparator(directory.Path), selected.CanonicalRoot);
        Assert.Equal("workspace with spaces", selected.DisplayName);
        Assert.Equal(selected, manager.Current);
    }

    [Fact]
    public void SelectRejectsRelativePath()
    {
        var manager = new WorkspaceManager();

        Assert.Throws<WorkspaceSelectionException>(() => manager.Select("relative-folder"));
        Assert.Null(manager.Current);
    }

    [Fact]
    public void SelectRejectsMissingDirectory()
    {
        var manager = new WorkspaceManager();
        var missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        Assert.Throws<WorkspaceSelectionException>(() => manager.Select(missing));
        Assert.Null(manager.Current);
    }

    [Fact]
    public void SelectRejectsFile()
    {
        using var directory = new TemporaryDirectory("file-parent");
        var file = Path.Combine(directory.Path, "not-a-folder.txt");
        File.WriteAllText(file, "test");

        Assert.Throws<WorkspaceSelectionException>(
            () => new WorkspaceManager().Select(file));
    }

    [Fact]
    public void SelectRejectsFilesystemRoot()
    {
        var root = Path.GetPathRoot(Path.GetTempPath())!;

        Assert.Throws<WorkspaceSelectionException>(
            () => new WorkspaceManager().Select(root));
    }

    [Fact]
    public void SelectRejectsUncAndDevicePathsOnWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        Assert.Throws<WorkspaceSelectionException>(
            () => new WorkspaceManager().Select(@"\\server\share\project"));
        Assert.Throws<WorkspaceSelectionException>(
            () => new WorkspaceManager().Select(@"\\?\C:\project"));
    }

    [Fact]
    public void TryRestoreLeavesCurrentEmptyWhenDirectoryDisappeared()
    {
        var manager = new WorkspaceManager();
        var missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        var restored = manager.TryRestore(missing, out var selection);

        Assert.False(restored);
        Assert.Null(selection);
        Assert.Null(manager.Current);
    }

    [Fact]
    public void FailedRestoreClearsAnOlderSelection()
    {
        using var directory = new TemporaryDirectory("valid-workspace");
        var manager = new WorkspaceManager();
        manager.Select(directory.Path);
        var missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

        manager.TryRestore(missing, out _);

        Assert.Null(manager.Current);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory(string leaf)
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "AgentLocalWeb.Tests",
                Guid.NewGuid().ToString("N"),
                leaf);
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            var testRoot = Directory.GetParent(Path)!.FullName;
            if (Directory.Exists(testRoot))
            {
                Directory.Delete(testRoot, recursive: true);
            }
        }
    }
}
