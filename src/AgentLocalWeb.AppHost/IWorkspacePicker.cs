namespace AgentLocalWeb.AppHost;

internal interface IWorkspacePicker
{
    Task<string?> PickAsync(string? initialDirectory, CancellationToken cancellationToken);
}
