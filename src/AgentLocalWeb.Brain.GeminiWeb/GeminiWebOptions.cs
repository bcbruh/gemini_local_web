namespace AgentLocalWeb.Brain.GeminiWeb;

public sealed class GeminiWebOptions
{
    public string? ChromeExecutablePath { get; init; }

    public string? ProfileDirectory { get; init; }

    public TimeSpan StartupTimeout { get; init; } = TimeSpan.FromSeconds(20);

    public TimeSpan ResponseTimeout { get; init; } = TimeSpan.FromMinutes(3);

    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(500);

    internal void Validate()
    {
        ValidatePositive(StartupTimeout, nameof(StartupTimeout));
        ValidatePositive(ResponseTimeout, nameof(ResponseTimeout));
        ValidatePositive(PollInterval, nameof(PollInterval));

        if (!string.IsNullOrWhiteSpace(ChromeExecutablePath) &&
            !File.Exists(ChromeExecutablePath))
        {
            throw new ArgumentException(
                "The configured Chrome executable does not exist.",
                nameof(ChromeExecutablePath));
        }
    }

    internal string ResolveProfileDirectory()
    {
        if (!string.IsNullOrWhiteSpace(ProfileDirectory))
        {
            return Path.GetFullPath(ProfileDirectory);
        }

        var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var production = Path.Combine(
            localData,
            "AgentLocalWeb",
            "GeminiWeb",
            "Chrome",
            "BrowserProfile");
        var spikeProfile = Path.Combine(
            localData,
            "AgentLocalWeb",
            "GeminiWebSpike",
            "Chrome",
            "BrowserProfile");

        // Preserve the profile used to validate the adapter instead of asking the user
        // to sign in again. New installations use the production directory.
        return Directory.Exists(spikeProfile) && !Directory.Exists(production)
            ? spikeProfile
            : production;
    }

    private static void ValidatePositive(TimeSpan value, string name)
    {
        if (value <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(name, "The duration must be positive.");
        }
    }
}
