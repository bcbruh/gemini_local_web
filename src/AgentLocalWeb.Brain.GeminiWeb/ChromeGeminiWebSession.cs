using System.Diagnostics;

namespace AgentLocalWeb.Brain.GeminiWeb;

internal sealed class ChromeGeminiWebSession : IGeminiWebSession
{
    private static readonly Uri GeminiUri = new("https://gemini.google.com/app");
    private readonly GeminiWebOptions _options;
    private readonly string _profileDirectory;
    private CdpClient? _cdpClient;
    private GeminiPageClient? _pageClient;
    private Process? _launchedProcess;
    private bool _ownsBrowser;
    private bool _disposed;

    public ChromeGeminiWebSession(GeminiWebOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
        _profileDirectory = _options.ResolveProfileDirectory();
    }

    public async ValueTask<GeminiWebSessionStatus> ConnectAsync(
        bool interactive,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_pageClient is not null)
        {
            var existingStatus = await GetStatusAsync(cancellationToken).ConfigureAwait(false);
            if (existingStatus == GeminiWebSessionStatus.Connected || interactive)
            {
                return existingStatus;
            }
        }

        await ResetTransportAsync().ConfigureAwait(false);
        Directory.CreateDirectory(_profileDirectory);

        var socketUri = await ChromeDevToolsEndpoint.TryFindGeminiPageAsync(
            _profileDirectory,
            TimeSpan.FromSeconds(3),
            cancellationToken).ConfigureAwait(false);
        if (socketUri is null)
        {
            LaunchChrome();
            socketUri = await WaitForGeminiPageAsync(cancellationToken).ConfigureAwait(false);
        }

        _cdpClient = new CdpClient();
        try
        {
            await _cdpClient.ConnectAsync(socketUri, cancellationToken).ConfigureAwait(false);
            _pageClient = new GeminiPageClient(_cdpClient, _options);
            await _pageClient.InitializeAsync(cancellationToken).ConfigureAwait(false);
            await NavigateToFreshConversationAsync(cancellationToken).ConfigureAwait(false);
            return await _pageClient.HasPromptAsync(cancellationToken).ConfigureAwait(false)
                ? GeminiWebSessionStatus.Connected
                : GeminiWebSessionStatus.ReconnectRequired;
        }
        catch
        {
            await ResetTransportAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask<GeminiWebSessionStatus> GetStatusAsync(
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_pageClient is null)
        {
            return GeminiWebSessionStatus.Disconnected;
        }

        return await _pageClient.HasPromptAsync(cancellationToken).ConfigureAwait(false)
            ? GeminiWebSessionStatus.Connected
            : GeminiWebSessionStatus.ReconnectRequired;
    }

    public async ValueTask<string> SendPromptAsync(
        string prompt,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_pageClient is null)
        {
            throw new GeminiWebSessionException(
                GeminiWebFailureKind.Authentication,
                "Gemini Web is not connected.");
        }

        return await _pageClient.SendPromptAsync(prompt, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisconnectAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_ownsBrowser && _cdpClient is not null)
        {
            try
            {
                await _cdpClient.SendCommandAsync("Browser.close", null, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (GeminiWebSessionException)
            {
                // Browser.close commonly closes the socket before returning its response.
            }
        }

        await ResetTransportAsync().ConfigureAwait(false);
        _launchedProcess?.Dispose();
        _launchedProcess = null;
        _ownsBrowser = false;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        await DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
        _disposed = true;
    }

    private void LaunchChrome()
    {
        var executable = !string.IsNullOrWhiteSpace(_options.ChromeExecutablePath)
            ? Path.GetFullPath(_options.ChromeExecutablePath)
            : ChromeDiscovery.FindExecutable();
        if (executable is null)
        {
            throw new GeminiWebSessionException(
                GeminiWebFailureKind.Unavailable,
                "Google Chrome was not found. Install Chrome or configure its executable path.");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add($"--user-data-dir={_profileDirectory}");
        startInfo.ArgumentList.Add("--no-first-run");
        startInfo.ArgumentList.Add("--no-default-browser-check");
        startInfo.ArgumentList.Add("--new-window");
        startInfo.ArgumentList.Add("--remote-debugging-address=127.0.0.1");
        startInfo.ArgumentList.Add("--remote-debugging-port=0");
        startInfo.ArgumentList.Add(GeminiUri.AbsoluteUri);

        try
        {
            _launchedProcess = Process.Start(startInfo) ?? throw new InvalidOperationException(
                "Chrome did not return a process handle.");
            _ownsBrowser = true;
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            throw new GeminiWebSessionException(
                GeminiWebFailureKind.Unavailable,
                "The isolated Chrome window could not be opened.",
                innerException: exception);
        }
    }

    private async Task NavigateToFreshConversationAsync(CancellationToken cancellationToken)
    {
        await _cdpClient!.SendCommandAsync(
            "Page.navigate",
            new { url = GeminiUri.AbsoluteUri },
            cancellationToken).ConfigureAwait(false);
        await Task.Delay(_options.PollInterval, cancellationToken).ConfigureAwait(false);
        using var startupTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        startupTimeout.CancelAfter(_options.StartupTimeout);
        try
        {
            while (!await _pageClient!.HasPromptAsync(startupTimeout.Token).ConfigureAwait(false))
            {
                await Task.Delay(_options.PollInterval, startupTimeout.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new GeminiWebSessionException(
                GeminiWebFailureKind.Authentication,
                "Gemini did not expose a prompt after opening a fresh conversation.",
                innerException: exception);
        }
    }

    private async Task<Uri> WaitForGeminiPageAsync(CancellationToken cancellationToken)
    {
        using var startupTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        startupTimeout.CancelAfter(_options.StartupTimeout);

        try
        {
            while (true)
            {
                var socketUri = await ChromeDevToolsEndpoint.TryFindGeminiPageAsync(
                    _profileDirectory,
                    TimeSpan.FromSeconds(3),
                    startupTimeout.Token).ConfigureAwait(false);
                if (socketUri is not null)
                {
                    return socketUri;
                }

                await Task.Delay(_options.PollInterval, startupTimeout.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new GeminiWebSessionException(
                GeminiWebFailureKind.Network,
                "Timed out while waiting for the Gemini page to open.",
                isRetryable: true,
                exception);
        }
    }

    private async ValueTask ResetTransportAsync()
    {
        _pageClient = null;
        if (_cdpClient is not null)
        {
            await _cdpClient.DisposeAsync().ConfigureAwait(false);
            _cdpClient = null;
        }
    }
}
