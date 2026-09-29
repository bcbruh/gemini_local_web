using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace AgentLocalWeb.Workspace;

public sealed class WorkspacePatchService
{
    public const int MaximumFiles = 10;
    public const int MaximumReplacementsPerFile = 50;
    public const int MaximumTotalProposedBytes = 2 * 1024 * 1024;
    public const int MaximumDiffBytes = 128 * 1024;

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly WorkspacePathResolver _resolver;
    private readonly ConcurrentDictionary<string, PreparedWorkspacePatch> _prepared =
        new(StringComparer.Ordinal);

    public WorkspacePatchService(WorkspaceSelection workspace)
    {
        _resolver = new WorkspacePathResolver(workspace);
    }

    public async Task<PreparedWorkspacePatch> PrepareAsync(
        IReadOnlyList<WorkspacePatchFileRequest> files,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(files);
        if (files.Count is < 1 or > MaximumFiles)
        {
            throw new WorkspacePatchException(
                WorkspacePatchError.InvalidPatch,
                $"A patch must contain between 1 and {MaximumFiles} files.");
        }

        var comparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        var seen = new HashSet<string>(comparer);
        var preparedFiles = new List<PreparedWorkspaceFile>(files.Count);
        var totalBytes = 0;

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateFileRequest(file);
            var resolved = _resolver.ResolveExisting(file.Path);
            if (resolved.Kind != WorkspacePathKind.File || !seen.Add(resolved.RelativePath))
            {
                throw new WorkspacePatchException(
                    WorkspacePatchError.InvalidPatch,
                    "Every patch target must be a distinct existing file.");
            }

            if (IsSuspectedSecret(resolved.RelativePath))
            {
                throw new WorkspacePatchException(
                    WorkspacePatchError.ContentBlocked,
                    "Secret-like files cannot be edited by the patch tool.");
            }

            var originalBytes = await ReadAllBytesAsync(resolved.RelativePath, cancellationToken)
                .ConfigureAwait(false);
            var actualHash = Sha256(originalBytes);
            if (!actualHash.Equals(file.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new WorkspacePatchException(
                    WorkspacePatchError.StaleFile,
                    $"'{resolved.RelativePath}' changed after it was read. Read it again and prepare a new patch.");
            }

            var hasBom = originalBytes.AsSpan().StartsWith(Encoding.UTF8.Preamble);
            var contentBytes = hasBom ? originalBytes.AsSpan(Encoding.UTF8.Preamble.Length) : originalBytes;
            string original;
            try
            {
                original = StrictUtf8.GetString(contentBytes);
            }
            catch (DecoderFallbackException exception)
            {
                throw new WorkspacePatchException(
                    WorkspacePatchError.ContentBlocked,
                    "Patch targets must be valid UTF-8 text files.",
                    exception);
            }

            var proposed = original;
            foreach (var replacement in file.Replacements)
            {
                var first = proposed.IndexOf(replacement.OldText, StringComparison.Ordinal);
                if (first < 0 || proposed.IndexOf(
                        replacement.OldText,
                        first + replacement.OldText.Length,
                        StringComparison.Ordinal) >= 0)
                {
                    throw new WorkspacePatchException(
                        WorkspacePatchError.InvalidPatch,
                        $"Each old_text must match exactly once in '{resolved.RelativePath}'.");
                }

                proposed = string.Concat(
                    proposed.AsSpan(0, first),
                    replacement.NewText,
                    proposed.AsSpan(first + replacement.OldText.Length));
            }

            var encoded = StrictUtf8.GetBytes(proposed);
            var proposedBytes = hasBom
                ? [.. Encoding.UTF8.Preamble, .. encoded]
                : encoded;
            totalBytes = checked(totalBytes + proposedBytes.Length);
            if (totalBytes > MaximumTotalProposedBytes)
            {
                throw new WorkspacePatchException(
                    WorkspacePatchError.InvalidPatch,
                    $"Proposed content exceeds the {MaximumTotalProposedBytes}-byte batch limit.");
            }

            preparedFiles.Add(new PreparedWorkspaceFile(
                resolved.RelativePath,
                actualHash,
                proposedBytes,
                CreateDiff(resolved.RelativePath, original, proposed)));
        }

        preparedFiles.Sort((left, right) => StringComparer.Ordinal.Compare(left.Path, right.Path));
        var diff = string.Join('\n', preparedFiles.Select(file => file.Diff));
        if (Encoding.UTF8.GetByteCount(diff) > MaximumDiffBytes)
        {
            throw new WorkspacePatchException(
                WorkspacePatchError.InvalidPatch,
                $"The diff preview exceeds the {MaximumDiffBytes}-byte limit.");
        }

        var actionHash = ComputeActionHash(preparedFiles);
        var patch = new PreparedWorkspacePatch(
            actionHash,
            diff,
            preparedFiles.Select(file => file.Path).ToArray(),
            preparedFiles);
        _prepared[actionHash] = patch;
        return patch;
    }

    public PreparedWorkspacePatch GetPrepared(string actionHash)
    {
        ValidateActionHash(actionHash);
        return _prepared.TryGetValue(actionHash, out var patch)
            ? patch
            : throw new WorkspacePatchException(
                WorkspacePatchError.NotPrepared,
                "The prepared patch is unavailable or was already applied.");
    }

    public async Task<AppliedWorkspacePatch> ApplyAsync(
        string actionHash,
        CancellationToken cancellationToken = default)
    {
        var patch = GetPrepared(actionHash);
        foreach (var file in patch.PreparedFiles)
        {
            await EnsureCurrentHashAsync(file, cancellationToken).ConfigureAwait(false);
        }

        var staged = new List<StagedFile>(patch.PreparedFiles.Count);
        var cleanupBackups = true;
        try
        {
            foreach (var file in patch.PreparedFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var resolved = _resolver.ResolveExisting(file.Path);
                var directory = Path.GetDirectoryName(resolved.PhysicalPath)!;
                var token = Guid.NewGuid().ToString("N");
                var temporary = Path.Combine(directory, $".agentlocalweb-{token}.tmp");
                var backup = Path.Combine(directory, $".agentlocalweb-{token}.bak");
                await File.WriteAllBytesAsync(temporary, file.ProposedBytes, cancellationToken)
                    .ConfigureAwait(false);
                staged.Add(new StagedFile(file, resolved.PhysicalPath, temporary, backup));
            }

            var replaced = new List<StagedFile>(staged.Count);
            try
            {
                foreach (var item in staged)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var current = _resolver.ResolveExisting(item.File.Path);
                    if (!string.Equals(
                            current.PhysicalPath,
                            item.TargetPath,
                            OperatingSystem.IsWindows()
                                ? StringComparison.OrdinalIgnoreCase
                                : StringComparison.Ordinal))
                    {
                        throw new WorkspacePatchException(
                            WorkspacePatchError.StaleFile,
                            $"'{item.File.Path}' resolved to a different file before apply.");
                    }

                    await EnsureCurrentHashAsync(item.File, cancellationToken).ConfigureAwait(false);
                    File.Replace(item.TemporaryPath, item.TargetPath, item.BackupPath);
                    replaced.Add(item);
                }
            }
            catch (Exception applyException)
            {
                Exception? rollbackException = null;
                foreach (var item in replaced.AsEnumerable().Reverse())
                {
                    try
                    {
                        File.Replace(item.BackupPath, item.TargetPath, null);
                    }
                    catch (Exception exception)
                    {
                        rollbackException ??= exception;
                    }
                }

                if (rollbackException is not null)
                {
                    cleanupBackups = false;
                    throw new WorkspacePatchException(
                        WorkspacePatchError.RollbackFailed,
                        "The patch failed and at least one file could not be restored.",
                        new AggregateException(applyException, rollbackException));
                }

                throw;
            }

            _prepared.TryRemove(actionHash, out _);
            return new AppliedWorkspacePatch(actionHash, patch.Paths);
        }
        catch (WorkspacePatchException)
        {
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (IOException exception)
        {
            throw new WorkspacePatchException(
                WorkspacePatchError.WriteFailed,
                "The prepared patch could not be applied.",
                exception);
        }
        finally
        {
            foreach (var item in staged)
            {
                TryDelete(item.TemporaryPath);
                if (cleanupBackups)
                {
                    TryDelete(item.BackupPath);
                }
            }
        }
    }

    private async Task EnsureCurrentHashAsync(
        PreparedWorkspaceFile file,
        CancellationToken cancellationToken)
    {
        var bytes = await ReadAllBytesAsync(file.Path, cancellationToken).ConfigureAwait(false);
        if (!Sha256(bytes).Equals(file.ExpectedSha256, StringComparison.Ordinal))
        {
            throw new WorkspacePatchException(
                WorkspacePatchError.StaleFile,
                $"'{file.Path}' changed after the patch was prepared. Nothing was applied.");
        }
    }

    private async Task<byte[]> ReadAllBytesAsync(
        string relativePath,
        CancellationToken cancellationToken)
    {
        await using var stream = _resolver.OpenRead(relativePath);
        if (stream.Length > WorkspaceReader.DefaultMaximumFileBytes)
        {
            throw new WorkspacePatchException(
                WorkspacePatchError.ContentBlocked,
                "Patch targets must fit within the workspace file size limit.");
        }

        var bytes = new byte[checked((int)stream.Length)];
        await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        if (bytes.AsSpan(0, Math.Min(bytes.Length, 8192)).Contains((byte)0))
        {
            throw new WorkspacePatchException(
                WorkspacePatchError.ContentBlocked,
                "Binary files cannot be edited by the patch tool.");
        }

        return bytes;
    }

    private static void ValidateFileRequest(WorkspacePatchFileRequest file)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (string.IsNullOrWhiteSpace(file.Path) ||
            file.ExpectedSha256.Length != 64 ||
            !file.ExpectedSha256.All(Uri.IsHexDigit) ||
            file.Replacements.Count is < 1 or > MaximumReplacementsPerFile ||
            file.Replacements.Any(replacement => string.IsNullOrEmpty(replacement.OldText)))
        {
            throw new WorkspacePatchException(
                WorkspacePatchError.InvalidPatch,
                "Patch files require a path, SHA-256 and bounded non-empty exact replacements.");
        }
    }

    private static void ValidateActionHash(string actionHash)
    {
        if (string.IsNullOrWhiteSpace(actionHash) ||
            actionHash.Length != 64 ||
            !actionHash.All(Uri.IsHexDigit))
        {
            throw new WorkspacePatchException(
                WorkspacePatchError.InvalidPatch,
                "The action_hash must be a SHA-256 value.");
        }
    }

    private static string ComputeActionHash(IReadOnlyList<PreparedWorkspaceFile> files)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in files)
        {
            Append(hash, file.Path);
            Append(hash, file.ExpectedSha256);
            Append(hash, Sha256(file.ProposedBytes));
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void Append(IncrementalHash hash, string value)
    {
        hash.AppendData(Encoding.UTF8.GetBytes(value));
        hash.AppendData([0]);
    }

    private static string Sha256(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static string CreateDiff(string path, string original, string proposed)
    {
        var before = SplitLines(original);
        var after = SplitLines(proposed);
        var prefix = 0;
        while (prefix < before.Length && prefix < after.Length && before[prefix] == after[prefix])
        {
            prefix++;
        }

        var suffix = 0;
        while (suffix < before.Length - prefix && suffix < after.Length - prefix &&
               before[^(suffix + 1)] == after[^(suffix + 1)])
        {
            suffix++;
        }

        var contextStart = Math.Max(0, prefix - 3);
        var beforeEnd = Math.Min(before.Length, before.Length - suffix + 3);
        var afterEnd = Math.Min(after.Length, after.Length - suffix + 3);
        var builder = new StringBuilder()
            .Append("--- a/").AppendLine(path.Replace('\\', '/'))
            .Append("+++ b/").AppendLine(path.Replace('\\', '/'))
            .Append("@@ -").Append(contextStart + 1).Append(',').Append(beforeEnd - contextStart)
            .Append(" +").Append(contextStart + 1).Append(',').Append(afterEnd - contextStart)
            .AppendLine(" @@");
        for (var index = contextStart; index < prefix; index++)
        {
            builder.Append(' ').AppendLine(before[index]);
        }

        for (var index = prefix; index < before.Length - suffix; index++)
        {
            builder.Append('-').AppendLine(before[index]);
        }

        for (var index = prefix; index < after.Length - suffix; index++)
        {
            builder.Append('+').AppendLine(after[index]);
        }

        for (var index = Math.Max(prefix, after.Length - suffix);
             index < afterEnd;
             index++)
        {
            builder.Append(' ').AppendLine(after[index]);
        }

        return builder.ToString().TrimEnd();
    }

    private static string[] SplitLines(string value) =>
        value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');

    private static bool IsSuspectedSecret(string path)
    {
        var name = Path.GetFileName(path);
        return name.Equals(".env", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith(".env.", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("id_rsa", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("id_ed25519", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("credentials.json", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".pem", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".pfx", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".key", StringComparison.OrdinalIgnoreCase);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed record StagedFile(
        PreparedWorkspaceFile File,
        string TargetPath,
        string TemporaryPath,
        string BackupPath);
}

public sealed record WorkspacePatchReplacement(string OldText, string NewText);

public sealed record WorkspacePatchFileRequest(
    string Path,
    string ExpectedSha256,
    IReadOnlyList<WorkspacePatchReplacement> Replacements);

public sealed class PreparedWorkspacePatch
{
    internal PreparedWorkspacePatch(
        string actionHash,
        string diff,
        IReadOnlyList<string> paths,
        IReadOnlyList<PreparedWorkspaceFile> preparedFiles)
    {
        ActionHash = actionHash;
        Diff = diff;
        Paths = paths;
        PreparedFiles = preparedFiles;
    }

    public string ActionHash { get; }

    public string Diff { get; }

    public IReadOnlyList<string> Paths { get; }

    internal IReadOnlyList<PreparedWorkspaceFile> PreparedFiles { get; }
}

internal sealed record PreparedWorkspaceFile(
    string Path,
    string ExpectedSha256,
    byte[] ProposedBytes,
    string Diff);

public sealed record AppliedWorkspacePatch(string ActionHash, IReadOnlyList<string> Paths);

public enum WorkspacePatchError
{
    InvalidPatch,
    ContentBlocked,
    StaleFile,
    NotPrepared,
    WriteFailed,
    RollbackFailed
}

public sealed class WorkspacePatchException(
    WorkspacePatchError error,
    string message,
    Exception? innerException = null) : Exception(message, innerException)
{
    public WorkspacePatchError Error { get; } = error;
}
