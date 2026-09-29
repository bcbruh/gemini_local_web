using System.Security.Cryptography;
using System.Text;

namespace AgentLocalWeb.Workspace;

public sealed class WorkspaceReader
{
    public const int DefaultMaximumFileBytes = 1024 * 1024;
    public const int MaximumReadLines = 500;
    public const int MaximumListEntries = 200;
    public const int MaximumSearchResults = 100;
    public const int MaximumScannedEntries = 10_000;

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private static readonly HashSet<string> IgnoredDirectoryNames = new(
        [".git", ".svn", ".hg", "node_modules", "bin", "obj", ".tools"],
        OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal);

    private readonly WorkspacePathResolver _resolver;
    private readonly int _maximumFileBytes;

    public WorkspaceReader(
        WorkspaceSelection workspace,
        int maximumFileBytes = DefaultMaximumFileBytes)
    {
        if (maximumFileBytes < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumFileBytes));
        }

        _resolver = new WorkspacePathResolver(workspace);
        _maximumFileBytes = maximumFileBytes;
    }

    public async Task<WorkspaceFileRead> ReadFileAsync(
        string relativePath,
        int startLine = 1,
        int? endLine = null,
        CancellationToken cancellationToken = default)
    {
        ValidateLineRange(startLine, endLine);
        if (IsSuspectedSecret(relativePath))
        {
            throw new WorkspaceReadException(
                WorkspaceReadError.SuspectedSecret,
                "The requested file may contain secrets and requires explicit policy approval.");
        }

        await using var stream = _resolver.OpenRead(relativePath);
        if (stream.Length > _maximumFileBytes)
        {
            throw new WorkspaceReadException(
                WorkspaceReadError.FileTooLarge,
                $"The requested file exceeds the {_maximumFileBytes}-byte read limit.");
        }

        var bytes = new byte[checked((int)stream.Length)];
        await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        if (bytes.AsSpan(0, Math.Min(bytes.Length, 8192)).Contains((byte)0))
        {
            throw new WorkspaceReadException(
                WorkspaceReadError.BinaryFile,
                "Binary files are not included in model context.");
        }

        string content;
        try
        {
            content = StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException exception)
        {
            throw new WorkspaceReadException(
                WorkspaceReadError.BinaryFile,
                "The file is not valid UTF-8 text.",
                exception);
        }

        var lines = SplitLines(content);
        var lastRequestedLine = endLine ?? Math.Min(
            lines.Count,
            checked(startLine + MaximumReadLines - 1));
        var actualStart = Math.Min(startLine, lines.Count + 1);
        var actualEnd = Math.Min(lastRequestedLine, lines.Count);
        var selectedLines = actualStart <= actualEnd
            ? lines.Skip(actualStart - 1).Take(actualEnd - actualStart + 1).ToArray()
            : [];
        var resolved = _resolver.ResolveExisting(relativePath);
        var fileInfo = new FileInfo(resolved.PhysicalPath);

        return new WorkspaceFileRead(
            NormalizeRelative(relativePath),
            actualStart,
            actualEnd,
            lines.Count,
            string.Join('\n', selectedLines),
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            bytes.LongLength,
            fileInfo.LastWriteTimeUtc);
    }

    public WorkspaceDirectoryList ListDirectory(
        string? relativePath = null,
        int maximumEntries = 100)
    {
        if (maximumEntries is < 1 or > MaximumListEntries)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumEntries));
        }

        var directory = _resolver.ResolveExisting(relativePath);
        if (directory.Kind != WorkspacePathKind.Directory)
        {
            throw new WorkspaceReadException(
                WorkspaceReadError.WrongKind,
                "The requested workspace path is not a directory.");
        }

        var all = new DirectoryInfo(directory.PhysicalPath)
            .EnumerateFileSystemInfos()
            .Where(entry => !IsIgnored(entry))
            .OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .Select(entry => TryCreateDirectoryEntry(entry))
            .Where(entry => entry is not null)
            .Cast<WorkspaceDirectoryEntry>()
            .ToArray();
        var entries = all.Take(maximumEntries).ToArray();

        return new WorkspaceDirectoryList(
            directory.RelativePath,
            entries,
            all.Length > entries.Length);
    }

    public WorkspaceFileSearch SearchFiles(
        string pattern,
        int maximumResults = 100,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);
        ValidateSearchLimit(maximumResults);
        if (pattern.Length > 200 || Path.IsPathRooted(pattern) || pattern.Contains(".."))
        {
            throw new WorkspaceReadException(
                WorkspaceReadError.InvalidSearch,
                "The file search pattern is invalid.");
        }

        var matches = new List<WorkspaceFileMatch>();
        var scanned = 0;
        var exhausted = true;
        foreach (var file in EnumerateSafeFiles(cancellationToken))
        {
            scanned++;
            if (scanned > MaximumScannedEntries)
            {
                exhausted = false;
                break;
            }

            var candidate = pattern.Contains('/') || pattern.Contains('\\')
                ? NormalizeForMatch(file.RelativePath)
                : Path.GetFileName(file.RelativePath);
            if (!WildcardMatch(NormalizeForMatch(pattern), candidate))
            {
                continue;
            }

            if (matches.Count == maximumResults)
            {
                exhausted = false;
                break;
            }

            matches.Add(new WorkspaceFileMatch(file.RelativePath, file.Size));
        }

        return new WorkspaceFileSearch(matches, !exhausted, Math.Min(scanned, MaximumScannedEntries));
    }

    public async Task<WorkspaceTextSearch> SearchTextAsync(
        string query,
        string filePattern = "*",
        bool caseSensitive = false,
        int maximumResults = 100,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        if (query.Length > 500)
        {
            throw new WorkspaceReadException(
                WorkspaceReadError.InvalidSearch,
                "The text search query exceeds the 500-character limit.");
        }

        ValidateSearchLimit(maximumResults);
        ValidateSearchPattern(filePattern);
        var results = new List<WorkspaceTextMatch>();
        var scanned = 0;
        var truncated = false;
        var comparison = caseSensitive
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;

        foreach (var file in EnumerateSafeFiles(cancellationToken))
        {
            if (scanned == MaximumScannedEntries)
            {
                truncated = true;
                break;
            }

            scanned++;
            var candidate = filePattern.Contains('/') || filePattern.Contains('\\')
                ? NormalizeForMatch(file.RelativePath)
                : Path.GetFileName(file.RelativePath);
            if (!WildcardMatch(NormalizeForMatch(filePattern), candidate) ||
                IsSuspectedSecret(file.RelativePath) ||
                file.Size > _maximumFileBytes)
            {
                continue;
            }

            string content;
            try
            {
                content = await ReadTextContentAsync(file.RelativePath, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (WorkspaceReadException exception) when (
                exception.Error is WorkspaceReadError.BinaryFile or
                    WorkspaceReadError.FileTooLarge or
                    WorkspaceReadError.SuspectedSecret)
            {
                continue;
            }

            var lines = SplitLines(content);
            for (var index = 0; index < lines.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!lines[index].Contains(query, comparison))
                {
                    continue;
                }

                if (results.Count == maximumResults)
                {
                    truncated = true;
                    return new WorkspaceTextSearch(results, truncated, scanned);
                }

                results.Add(new WorkspaceTextMatch(
                    file.RelativePath,
                    index + 1,
                    TruncateLine(lines[index], 300)));
            }
        }

        return new WorkspaceTextSearch(results, truncated, scanned);
    }

    internal static bool IsSuspectedSecret(string path)
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

    private static bool IsIgnored(FileSystemInfo entry) =>
        entry is DirectoryInfo && IgnoredDirectoryNames.Contains(entry.Name);

    private WorkspaceDirectoryEntry? TryCreateDirectoryEntry(FileSystemInfo entry)
    {
        var lexicalRelative = Path.GetRelativePath(_resolver.PhysicalRoot, entry.FullName);
        ResolvedWorkspacePath resolved;
        try
        {
            resolved = _resolver.ResolveExisting(lexicalRelative);
        }
        catch (WorkspacePathException exception) when (
            exception.Error is WorkspacePathError.OutsideWorkspace or
                WorkspacePathError.ReparsePointNotResolved or
                WorkspacePathError.NotFound)
        {
            return null;
        }

        return new WorkspaceDirectoryEntry(
            resolved.RelativePath,
            entry.Name,
            resolved.Kind,
            resolved.Kind == WorkspacePathKind.File
                ? new FileInfo(resolved.PhysicalPath).Length
                : null);
    }

    private IEnumerable<SafeWorkspaceFile> EnumerateSafeFiles(
        CancellationToken cancellationToken)
    {
        var pending = new Queue<string>();
        var visited = new HashSet<string>(
            OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal);
        pending.Enqueue(_resolver.PhysicalRoot);
        visited.Add(_resolver.PhysicalRoot);
        var encountered = 0;

        while (pending.TryDequeue(out var directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var entry in new DirectoryInfo(directory)
                .EnumerateFileSystemInfos()
                .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                encountered++;
                if (encountered > MaximumScannedEntries)
                {
                    yield break;
                }

                if (IsIgnored(entry))
                {
                    continue;
                }

                var lexicalRelative = Path.GetRelativePath(_resolver.PhysicalRoot, entry.FullName);
                ResolvedWorkspacePath resolved;
                try
                {
                    resolved = _resolver.ResolveExisting(lexicalRelative);
                }
                catch (WorkspacePathException exception) when (
                    exception.Error is WorkspacePathError.OutsideWorkspace or
                        WorkspacePathError.ReparsePointNotResolved or
                        WorkspacePathError.NotFound)
                {
                    continue;
                }

                if (resolved.Kind == WorkspacePathKind.Directory)
                {
                    if (visited.Add(resolved.PhysicalPath))
                    {
                        pending.Enqueue(resolved.PhysicalPath);
                    }

                    continue;
                }

                var info = new FileInfo(resolved.PhysicalPath);
                yield return new SafeWorkspaceFile(
                    resolved.RelativePath,
                    info.Length);
            }
        }
    }

    private async Task<string> ReadTextContentAsync(
        string relativePath,
        CancellationToken cancellationToken)
    {
        if (IsSuspectedSecret(relativePath))
        {
            throw new WorkspaceReadException(
                WorkspaceReadError.SuspectedSecret,
                "The requested file may contain secrets.");
        }

        await using var stream = _resolver.OpenRead(relativePath);
        if (stream.Length > _maximumFileBytes)
        {
            throw new WorkspaceReadException(
                WorkspaceReadError.FileTooLarge,
                "The requested file exceeds the read limit.");
        }

        var bytes = new byte[checked((int)stream.Length)];
        await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        if (bytes.AsSpan(0, Math.Min(bytes.Length, 8192)).Contains((byte)0))
        {
            throw new WorkspaceReadException(
                WorkspaceReadError.BinaryFile,
                "Binary files are not searchable text.");
        }

        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException exception)
        {
            throw new WorkspaceReadException(
                WorkspaceReadError.BinaryFile,
                "The file is not valid UTF-8 text.",
                exception);
        }
    }

    private static bool WildcardMatch(string pattern, string value)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var previous = new bool[value.Length + 1];
        previous[0] = true;
        foreach (var patternCharacter in pattern)
        {
            var current = new bool[value.Length + 1];
            if (patternCharacter == '*')
            {
                current[0] = previous[0];
                for (var valueIndex = 1; valueIndex <= value.Length; valueIndex++)
                {
                    current[valueIndex] = previous[valueIndex] || current[valueIndex - 1];
                }
            }
            else
            {
                for (var valueIndex = 1; valueIndex <= value.Length; valueIndex++)
                {
                    current[valueIndex] = previous[valueIndex - 1] &&
                        (patternCharacter == '?' ||
                         value[valueIndex - 1].ToString().Equals(
                             patternCharacter.ToString(),
                             comparison));
                }
            }

            previous = current;
        }

        return previous[value.Length];
    }

    private static void ValidateSearchLimit(int maximumResults)
    {
        if (maximumResults is < 1 or > MaximumSearchResults)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumResults));
        }
    }

    private static void ValidateSearchPattern(string pattern)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);
        if (pattern.Length > 200 || Path.IsPathRooted(pattern) || pattern.Contains(".."))
        {
            throw new WorkspaceReadException(
                WorkspaceReadError.InvalidSearch,
                "The file search pattern is invalid.");
        }
    }

    private static string NormalizeForMatch(string value) =>
        value.Replace('\\', '/');

    private static string TruncateLine(string line, int maximumCharacters) =>
        line.Length <= maximumCharacters
            ? line
            : string.Concat(line.AsSpan(0, maximumCharacters), "…");

    private static void ValidateLineRange(int startLine, int? endLine)
    {
        if (startLine < 1 || endLine < startLine ||
            endLine - startLine + 1 > MaximumReadLines)
        {
            throw new WorkspaceReadException(
                WorkspaceReadError.InvalidRange,
                $"Line ranges must be positive and contain at most {MaximumReadLines} lines.");
        }
    }

    private static List<string> SplitLines(string content)
    {
        var lines = content.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n')
            .ToList();
        if (lines.Count > 1 && lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        return lines;
    }

    private static string NormalizeRelative(string path) =>
        path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);

    private sealed record SafeWorkspaceFile(string RelativePath, long Size);
}

public enum WorkspaceReadError
{
    WrongKind,
    InvalidRange,
    FileTooLarge,
    BinaryFile,
    SuspectedSecret,
    InvalidSearch
}

public sealed class WorkspaceReadException(
    WorkspaceReadError error,
    string message,
    Exception? innerException = null) : Exception(message, innerException)
{
    public WorkspaceReadError Error { get; } = error;
}

public sealed record WorkspaceFileRead(
    string Path,
    int StartLine,
    int EndLine,
    int TotalLines,
    string Content,
    string Sha256,
    long Size,
    DateTime LastModifiedUtc);

public sealed record WorkspaceDirectoryEntry(
    string Path,
    string Name,
    WorkspacePathKind Kind,
    long? Size);

public sealed record WorkspaceDirectoryList(
    string Path,
    IReadOnlyList<WorkspaceDirectoryEntry> Entries,
    bool Truncated);

public sealed record WorkspaceFileMatch(string Path, long Size);

public sealed record WorkspaceFileSearch(
    IReadOnlyList<WorkspaceFileMatch> Matches,
    bool Truncated,
    int ScannedEntries);

public sealed record WorkspaceTextMatch(
    string Path,
    int Line,
    string Preview);

public sealed record WorkspaceTextSearch(
    IReadOnlyList<WorkspaceTextMatch> Matches,
    bool Truncated,
    int ScannedFiles);
