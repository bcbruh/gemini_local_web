using System.Diagnostics;
using AgentLocalWeb.Workspace;

namespace AgentLocalWeb.Workspace.Tests;

public sealed class WorkspacePathResolverTests
{
    [Fact]
    public void ResolvesExistingFileAndWorkspaceRoot()
    {
        using var fixture = new WorkspaceFixture();
        var file = fixture.Write("src/app.cs", "content");
        var resolver = fixture.CreateResolver();

        var root = resolver.ResolveExisting(string.Empty);
        var resolved = resolver.ResolveExisting("src/app.cs");

        Assert.Equal(WorkspacePathKind.Directory, root.Kind);
        Assert.Equal(string.Empty, root.RelativePath);
        Assert.Equal(WorkspacePathKind.File, resolved.Kind);
        Assert.Equal(Path.GetFullPath(file), resolved.PhysicalPath);
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("src/../../outside.txt")]
    [InlineData("src/../app.cs")]
    public void RejectsTraversalEvenWhenItWouldNormalizeInside(string path)
    {
        using var fixture = new WorkspaceFixture();
        var resolver = fixture.CreateResolver();

        var exception = Assert.Throws<WorkspacePathException>(
            () => resolver.ResolveExisting(path));

        Assert.Equal(WorkspacePathError.InvalidRelativePath, exception.Error);
    }

    [Fact]
    public void RejectsAbsoluteUncDeviceAndAlternateDataStreamPaths()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new WorkspaceFixture();
        var resolver = fixture.CreateResolver();
        var paths = new[]
        {
            @"C:\Windows\win.ini",
            @"\\server\share\file.txt",
            @"\\?\C:\Windows\win.ini",
            "file.txt:secret"
        };

        foreach (var path in paths)
        {
            var exception = Assert.Throws<WorkspacePathException>(
                () => resolver.ResolveExisting(path));
            Assert.Equal(WorkspacePathError.InvalidRelativePath, exception.Error);
        }
    }

    [Fact]
    public void UsesWindowsCaseInsensitivePathSemantics()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var fixture = new WorkspaceFixture();
        fixture.Write("Source/Program.cs", "content");
        var resolver = fixture.CreateResolver();

        var resolved = resolver.ResolveExisting("SOURCE/PROGRAM.CS");

        Assert.Equal(WorkspacePathKind.File, resolved.Kind);
    }

    [Fact]
    public void RejectsLinkToSiblingWithCommonWorkspacePrefix()
    {
        using var fixture = new WorkspaceFixture();
        var outside = fixture.CreateSibling("workspace-evil");
        File.WriteAllText(Path.Combine(outside, "secret.txt"), "secret");
        var link = Path.Combine(fixture.Workspace, "escape");
        if (!TryCreateDirectoryLink(link, outside))
        {
            return;
        }

        var exception = Assert.Throws<WorkspacePathException>(
            () => fixture.CreateResolver().ResolveExisting("escape/secret.txt"));

        Assert.Equal(WorkspacePathError.OutsideWorkspace, exception.Error);
    }

    [Fact]
    public void AllowsLinkWhoseFinalTargetRemainsInsideWorkspace()
    {
        using var fixture = new WorkspaceFixture();
        var actual = Path.Combine(fixture.Workspace, "actual");
        Directory.CreateDirectory(actual);
        File.WriteAllText(Path.Combine(actual, "readme.txt"), "safe");
        var link = Path.Combine(fixture.Workspace, "alias");
        if (!TryCreateDirectoryLink(link, actual))
        {
            return;
        }

        var resolved = fixture.CreateResolver().ResolveExisting("alias/readme.txt");

        Assert.Equal(Path.Combine(actual, "readme.txt"), resolved.PhysicalPath);
    }

    private static bool TryCreateDirectoryLink(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
            return true;
        }
        catch (UnauthorizedAccessException) when (OperatingSystem.IsWindows())
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                ArgumentList = { "/c", "mklink", "/J", link, target }
            });
            process?.WaitForExit();
            return process?.ExitCode == 0 && Directory.Exists(link);
        }
        catch (IOException)
        {
            return false;
        }
    }

    private sealed class WorkspaceFixture : IDisposable
    {
        private readonly string _testRoot = Path.Combine(
            Path.GetTempPath(),
            "AgentLocalWeb.PathTests",
            Guid.NewGuid().ToString("N"));

        public WorkspaceFixture()
        {
            Workspace = Path.Combine(_testRoot, "workspace");
            Directory.CreateDirectory(Workspace);
        }

        public string Workspace { get; }

        public WorkspacePathResolver CreateResolver() =>
            new(new WorkspaceManager().Select(Workspace));

        public string Write(string relativePath, string content)
        {
            var path = Path.Combine(Workspace, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
            return path;
        }

        public string CreateSibling(string name)
        {
            var path = Path.Combine(_testRoot, name);
            Directory.CreateDirectory(path);
            return path;
        }

        public void Dispose()
        {
            if (Directory.Exists(_testRoot))
            {
                Directory.Delete(_testRoot, recursive: true);
            }
        }
    }
}
