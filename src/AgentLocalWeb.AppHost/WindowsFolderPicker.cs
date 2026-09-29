using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace AgentLocalWeb.AppHost;

internal sealed partial class WindowsFolderPicker : IWorkspacePicker, IDisposable
{
    private readonly SemaphoreSlim _singlePicker = new(1, 1);

    public async Task<string?> PickAsync(
        string? initialDirectory,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Capture this before any semaphore wait can resume on a pool thread.
        var ownerHandle = NativeMethods.GetForegroundWindow();
        await _singlePicker.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Capture the browser window before moving to the dedicated STA thread. Using it
            // as the owner keeps the native picker in front instead of hiding it behind Chrome.
            var completion = new TaskCompletionSource<string?>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(() => ShowDialog(initialDirectory, ownerHandle, completion))
            {
                IsBackground = true,
                Name = "AgentLocalWeb workspace picker"
            };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            return await completion.Task.ConfigureAwait(false);
        }
        finally
        {
            _singlePicker.Release();
        }
    }

    public void Dispose() => _singlePicker.Dispose();

    private static void ShowDialog(
        string? initialDirectory,
        nint ownerHandle,
        TaskCompletionSource<string?> completion)
    {
        try
        {
            using var dialog = new FolderBrowserDialog
            {
                Description = "Select the workspace AgentLocalWeb may access",
                ShowNewFolderButton = false,
                UseDescriptionForTitle = true
            };
            if (!string.IsNullOrWhiteSpace(initialDirectory) &&
                Directory.Exists(initialDirectory))
            {
                dialog.InitialDirectory = initialDirectory;
            }

            var result = ownerHandle == nint.Zero
                ? dialog.ShowDialog()
                : dialog.ShowDialog(new WindowHandle(ownerHandle));
            completion.TrySetResult(result == DialogResult.OK ? dialog.SelectedPath : null);
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
        }
    }

    private sealed class WindowHandle(nint handle) : IWin32Window
    {
        public nint Handle { get; } = handle;
    }

    private static partial class NativeMethods
    {
        [LibraryImport("user32.dll")]
        internal static partial nint GetForegroundWindow();
    }
}
