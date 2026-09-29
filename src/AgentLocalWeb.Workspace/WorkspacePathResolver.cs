using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace AgentLocalWeb.Workspace;

public sealed class WorkspacePathResolver
{
    private readonly string _physicalRoot;
    private readonly StringComparison _pathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    public WorkspacePathResolver(WorkspaceSelection workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        _physicalRoot = ResolveRoot(workspace.CanonicalRoot);
    }

    public string PhysicalRoot => _physicalRoot;

    public FileStream OpenRead(string relativePath)
    {
        var resolved = ResolveExisting(relativePath);
        if (resolved.Kind != WorkspacePathKind.File)
        {
            throw new WorkspacePathException(
                WorkspacePathError.WrongKind,
                "The workspace path is not a file.");
        }

        FileStream? stream = null;
        try
        {
            stream = new FileStream(
                resolved.PhysicalPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (OperatingSystem.IsWindows())
            {
                EnsureInsideRoot(GetFinalPath(stream.SafeFileHandle));
            }

            return stream;
        }
        catch
        {
            stream?.Dispose();
            throw;
        }
    }

    public ResolvedWorkspacePath ResolveExisting(string? relativePath)
    {
        var segments = ValidateRelativePath(relativePath);
        var current = _physicalRoot;

        foreach (var segment in segments)
        {
            var candidate = Path.Combine(current, segment);
            var info = GetExistingInfo(candidate);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                info = info.ResolveLinkTarget(returnFinalTarget: true)
                    ?? throw new WorkspacePathException(
                        WorkspacePathError.ReparsePointNotResolved,
                        "A workspace link could not be resolved safely.");
            }

            current = Path.GetFullPath(info.FullName);
            EnsureInsideRoot(current);
        }

        var kind = Directory.Exists(current)
            ? WorkspacePathKind.Directory
            : WorkspacePathKind.File;
        var normalizedRelativePath = Path.GetRelativePath(_physicalRoot, current);
        if (normalizedRelativePath == ".")
        {
            normalizedRelativePath = string.Empty;
        }

        return new ResolvedWorkspacePath(normalizedRelativePath, current, kind);
    }

    private IReadOnlyList<string> ValidateRelativePath(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || relativePath == ".")
        {
            return [];
        }

        if (Path.IsPathRooted(relativePath) || Path.IsPathFullyQualified(relativePath))
        {
            throw InvalidPath("Workspace paths must be relative.");
        }

        if (OperatingSystem.IsWindows() &&
            (relativePath.StartsWith("\\", StringComparison.Ordinal) ||
             relativePath.Contains(':', StringComparison.Ordinal)))
        {
            throw InvalidPath("Device, network and alternate data stream paths are not allowed.");
        }

        var segments = relativePath.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 ||
            segments.Any(segment => segment is "." or ".." ||
                segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
        {
            throw InvalidPath("The workspace-relative path is invalid.");
        }

        return segments;
    }

    private void EnsureInsideRoot(string candidate)
    {
        var relative = Path.GetRelativePath(_physicalRoot, candidate);
        if (Path.IsPathRooted(relative) ||
            relative.Equals("..", _pathComparison) ||
            relative.StartsWith($"..{Path.DirectorySeparatorChar}", _pathComparison) ||
            relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", _pathComparison))
        {
            throw new WorkspacePathException(
                WorkspacePathError.OutsideWorkspace,
                "The resolved path is outside the selected workspace.");
        }
    }

    private static FileSystemInfo GetExistingInfo(string path)
    {
        if (Directory.Exists(path))
        {
            return new DirectoryInfo(path);
        }

        if (File.Exists(path))
        {
            return new FileInfo(path);
        }

        throw new WorkspacePathException(
            WorkspacePathError.NotFound,
            "The workspace path does not exist.");
    }

    private static string ResolveRoot(string root)
    {
        var info = new DirectoryInfo(root);
        if (!info.Exists)
        {
            throw new WorkspacePathException(
                WorkspacePathError.NotFound,
                "The workspace root no longer exists.");
        }

        if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            info = (DirectoryInfo)(info.ResolveLinkTarget(returnFinalTarget: true)
                ?? throw new WorkspacePathException(
                    WorkspacePathError.ReparsePointNotResolved,
                    "The workspace root link could not be resolved safely."));
        }

        var resolved = Path.TrimEndingDirectorySeparator(Path.GetFullPath(info.FullName));
        return OperatingSystem.IsWindows()
            ? GetFinalDirectoryPath(resolved)
            : resolved;
    }

    private static string GetFinalDirectoryPath(string path)
    {
        const uint shareAll = 0x00000001 | 0x00000002 | 0x00000004;
        const uint openExisting = 3;
        const uint backupSemantics = 0x02000000;
        using var handle = CreateFileW(
            path,
            0,
            shareAll,
            IntPtr.Zero,
            openExisting,
            backupSemantics,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            throw new WorkspacePathException(
                WorkspacePathError.ReparsePointNotResolved,
                "The workspace root could not be opened safely.",
                new Win32Exception(Marshal.GetLastWin32Error()));
        }

        return Path.TrimEndingDirectorySeparator(GetFinalPath(handle));
    }

    private static string GetFinalPath(SafeFileHandle handle)
    {
        var buffer = new StringBuilder(512);
        var length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 0);
        if (length == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        if (length >= buffer.Capacity)
        {
            buffer.EnsureCapacity(checked((int)length + 1));
            length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 0);
            if (length == 0 || length >= buffer.Capacity)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
        }

        var path = buffer.ToString();
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
        {
            return @"\\" + path[8..];
        }

        return path.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase)
            ? path[4..]
            : path;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(
        SafeFileHandle file,
        StringBuilder filePath,
        uint filePathLength,
        uint flags);

    private static WorkspacePathException InvalidPath(string message) =>
        new(WorkspacePathError.InvalidRelativePath, message);
}

public enum WorkspacePathKind
{
    File,
    Directory
}

public enum WorkspacePathError
{
    InvalidRelativePath,
    NotFound,
    OutsideWorkspace,
    ReparsePointNotResolved,
    WrongKind
}

public sealed record ResolvedWorkspacePath(
    string RelativePath,
    string PhysicalPath,
    WorkspacePathKind Kind);

public sealed class WorkspacePathException(
    WorkspacePathError error,
    string message,
    Exception? innerException = null) : Exception(message, innerException)
{
    public WorkspacePathError Error { get; } = error;
}
