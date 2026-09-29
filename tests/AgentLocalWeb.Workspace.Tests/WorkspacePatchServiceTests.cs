using System.Security.Cryptography;
using System.Text;
using AgentLocalWeb.Workspace;

namespace AgentLocalWeb.Workspace.Tests;

public sealed class WorkspacePatchServiceTests
{
    [Fact]
    public async Task PreviewsAndAppliesExactMultiFilePatch()
    {
        using var fixture = new PatchFixture();
        fixture.Write("a.txt", "alpha\r\nbeta\r\n");
        fixture.Write("src/b.txt", "one\ntwo\n");
        var service = fixture.CreateService();

        var patch = await service.PrepareAsync(
            [
                FileRequest("a.txt", fixture.Hash("a.txt"), "beta", "BETA"),
                FileRequest("src/b.txt", fixture.Hash("src/b.txt"), "two", "TWO")
            ],
            TestContext.Current.CancellationToken);

        Assert.Equal(64, patch.ActionHash.Length);
        Assert.Contains("--- a/a.txt", patch.Diff, StringComparison.Ordinal);
        Assert.Contains("-beta", patch.Diff, StringComparison.Ordinal);
        Assert.Contains("+BETA", patch.Diff, StringComparison.Ordinal);

        var applied = await service.ApplyAsync(
            patch.ActionHash,
            TestContext.Current.CancellationToken);

        Assert.Equal(["a.txt", Path.Combine("src", "b.txt")], applied.Paths);
        Assert.Equal("alpha\r\nBETA\r\n", fixture.Read("a.txt"));
        Assert.Equal("one\nTWO\n", fixture.Read("src/b.txt"));
    }

    [Fact]
    public async Task StaleFilePreventsEveryWriteInBatch()
    {
        using var fixture = new PatchFixture();
        fixture.Write("a.txt", "before-a");
        fixture.Write("b.txt", "before-b");
        var service = fixture.CreateService();
        var patch = await service.PrepareAsync(
            [
                FileRequest("a.txt", fixture.Hash("a.txt"), "before-a", "after-a"),
                FileRequest("b.txt", fixture.Hash("b.txt"), "before-b", "after-b")
            ],
            TestContext.Current.CancellationToken);
        fixture.Write("b.txt", "user edit");

        var exception = await Assert.ThrowsAsync<WorkspacePatchException>(() =>
            service.ApplyAsync(patch.ActionHash, TestContext.Current.CancellationToken));

        Assert.Equal(WorkspacePatchError.StaleFile, exception.Error);
        Assert.Equal("before-a", fixture.Read("a.txt"));
        Assert.Equal("user edit", fixture.Read("b.txt"));
    }

    [Fact]
    public async Task RejectsAmbiguousReplacementAndSecretTarget()
    {
        using var fixture = new PatchFixture();
        fixture.Write("repeat.txt", "same same");
        fixture.Write(".env", "TOKEN=value");
        var service = fixture.CreateService();

        var ambiguous = await Assert.ThrowsAsync<WorkspacePatchException>(() =>
            service.PrepareAsync(
                [FileRequest("repeat.txt", fixture.Hash("repeat.txt"), "same", "new")],
                TestContext.Current.CancellationToken));
        var secret = await Assert.ThrowsAsync<WorkspacePatchException>(() =>
            service.PrepareAsync(
                [FileRequest(".env", fixture.Hash(".env"), "value", "other")],
                TestContext.Current.CancellationToken));

        Assert.Equal(WorkspacePatchError.InvalidPatch, ambiguous.Error);
        Assert.Equal(WorkspacePatchError.ContentBlocked, secret.Error);
    }

    private static WorkspacePatchFileRequest FileRequest(
        string path,
        string hash,
        string oldText,
        string newText) =>
        new(path, hash, [new WorkspacePatchReplacement(oldText, newText)]);

    private sealed class PatchFixture : IDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(),
            "AgentLocalWeb.PatchTests",
            Guid.NewGuid().ToString("N"));

        public PatchFixture() => Directory.CreateDirectory(_root);

        public WorkspacePatchService CreateService() =>
            new(new WorkspaceManager().Select(_root));

        public void Write(string relativePath, string content)
        {
            var path = Path.Combine(_root, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content, new UTF8Encoding(false));
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
}
