namespace AgentLocalWeb.Workspace;

public sealed class WorkspaceManager
{
    private readonly object _sync = new();
    private WorkspaceSelection? _current;

    public WorkspaceSelection? Current
    {
        get
        {
            lock (_sync)
            {
                return _current;
            }
        }
    }

    public WorkspaceSelection Select(string path)
    {
        var selection = Validate(path);
        lock (_sync)
        {
            _current = selection;
        }

        return selection;
    }

    public bool TryRestore(string? path, out WorkspaceSelection? selection)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            Clear();
            selection = null;
            return false;
        }

        try
        {
            selection = Select(path);
            return true;
        }
        catch (WorkspaceSelectionException)
        {
            Clear();
            selection = null;
            return false;
        }
    }

    private void Clear()
    {
        lock (_sync)
        {
            _current = null;
        }
    }

    public static WorkspaceSelection Validate(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new WorkspaceSelectionException("A workspace folder is required.");
        }

        if (!Path.IsPathFullyQualified(path))
        {
            throw new WorkspaceSelectionException("The workspace path must be absolute.");
        }

        if (OperatingSystem.IsWindows() && path.StartsWith("\\\\", StringComparison.Ordinal))
        {
            throw new WorkspaceSelectionException(
                "Network and Windows device paths are not supported as workspaces.");
        }

        string canonicalRoot;
        try
        {
            canonicalRoot = Path.TrimEndingDirectorySeparator(
                new DirectoryInfo(path).FullName);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new WorkspaceSelectionException("The workspace path is invalid.", exception);
        }

        if (!Directory.Exists(canonicalRoot))
        {
            throw new WorkspaceSelectionException("The workspace folder no longer exists.");
        }

        if (string.Equals(
            canonicalRoot,
            Path.GetPathRoot(canonicalRoot),
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal))
        {
            throw new WorkspaceSelectionException(
                "A filesystem or volume root cannot be selected as the workspace.");
        }

        var name = new DirectoryInfo(canonicalRoot).Name;
        if (string.IsNullOrEmpty(name))
        {
            name = canonicalRoot;
        }

        return new WorkspaceSelection(canonicalRoot, name);
    }
}

public sealed class WorkspaceSelectionException : Exception
{
    public WorkspaceSelectionException(string message)
        : base(message)
    {
    }

    public WorkspaceSelectionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
