using System.Security.Cryptography;
using Microsoft.AspNetCore.WebUtilities;

namespace AgentLocalWeb.AppHost;

internal sealed class LocalAccessSession(TimeProvider timeProvider)
{
    public const string CookieName = "AgentLocalWeb.Session";
    public const string CsrfHeaderName = "X-AgentLocalWeb-CSRF";

    private readonly object _sync = new();
    private readonly byte[] _bootstrapToken = RandomNumberGenerator.GetBytes(32);
    private readonly DateTimeOffset _bootstrapExpiresAt = timeProvider.GetUtcNow().AddMinutes(2);
    private byte[]? _sessionToken;
    private string? _sessionTokenText;
    private string? _csrfToken;
    private bool _bootstrapConsumed;

    public string BootstrapToken => WebEncoders.Base64UrlEncode(_bootstrapToken);

    public SessionCredentials? TryBootstrap(string? candidate)
    {
        if (!TryDecode(candidate, out var candidateBytes))
        {
            return null;
        }

        lock (_sync)
        {
            if (_bootstrapConsumed ||
                timeProvider.GetUtcNow() > _bootstrapExpiresAt ||
                !CryptographicOperations.FixedTimeEquals(candidateBytes, _bootstrapToken))
            {
                return null;
            }

            _bootstrapConsumed = true;
            _sessionToken = RandomNumberGenerator.GetBytes(32);
            _sessionTokenText = WebEncoders.Base64UrlEncode(_sessionToken);
            _csrfToken = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
            return new SessionCredentials(_sessionTokenText, _csrfToken);
        }
    }

    public bool IsAuthenticated(string? candidate)
    {
        if (!TryDecode(candidate, out var candidateBytes))
        {
            return false;
        }

        lock (_sync)
        {
            return _sessionToken is not null &&
                CryptographicOperations.FixedTimeEquals(candidateBytes, _sessionToken);
        }
    }

    public bool IsValidCsrf(string? candidate)
    {
        lock (_sync)
        {
            return _csrfToken is not null &&
                candidate is not null &&
                CryptographicOperations.FixedTimeEquals(
                    System.Text.Encoding.UTF8.GetBytes(candidate),
                    System.Text.Encoding.UTF8.GetBytes(_csrfToken));
        }
    }

    public string? GetCsrfToken(string? sessionToken)
    {
        lock (_sync)
        {
            return IsAuthenticated(sessionToken) ? _csrfToken : null;
        }
    }

    private static bool TryDecode(string? value, out byte[] bytes)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            bytes = [];
            return false;
        }

        try
        {
            bytes = WebEncoders.Base64UrlDecode(value);
            return true;
        }
        catch (FormatException)
        {
            bytes = [];
            return false;
        }
    }
}

internal sealed record SessionCredentials(string SessionToken, string CsrfToken);
