namespace AgentLocalWeb.Brain.GeminiWeb;

/// <summary>
/// Gemini Web brain backed by a visible Chrome window and an application-owned profile.
/// Browser credentials remain inside that profile and are never exposed through this API.
/// </summary>
public sealed class GeminiWebBrain : IBrain
{
    private readonly IGeminiWebSession _session;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private BrainStatus _status;
    private bool _disposed;

    public GeminiWebBrain()
        : this(new ChromeGeminiWebSession(new GeminiWebOptions()))
    {
    }

    public GeminiWebBrain(GeminiWebOptions options)
        : this(new ChromeGeminiWebSession(options ?? throw new ArgumentNullException(nameof(options))))
    {
    }

    internal GeminiWebBrain(IGeminiWebSession session)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _status = Status(BrainConnectionState.Disconnected, "Gemini Web is disconnected.");
    }

    public string Provider => "gemini-web";

    public async ValueTask<BrainStatus> GetStatusAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_status.State is BrainConnectionState.Disconnected or BrainConnectionState.Unavailable)
            {
                return _status;
            }

            _status = ToBrainStatus(await _session.GetStatusAsync(cancellationToken)
                .ConfigureAwait(false));
            return _status;
        }
        catch (GeminiWebSessionException exception)
        {
            _status = StatusFor(exception);
            return _status;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<BrainStatus> ConnectAsync(
        BrainConnectionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (request.ForceReconnect)
            {
                await _session.DisconnectAsync(cancellationToken).ConfigureAwait(false);
            }

            _status = Status(BrainConnectionState.Connecting, "Opening the isolated Gemini browser profile.");
            var sessionStatus = await _session.ConnectAsync(request.Interactive, cancellationToken)
                .ConfigureAwait(false);
            _status = ToBrainStatus(sessionStatus);
            return _status;
        }
        catch (GeminiWebSessionException exception)
        {
            _status = StatusFor(exception);
            throw ToBrainException(exception);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisconnectAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _session.DisconnectAsync(cancellationToken).ConfigureAwait(false);
            _status = Status(
                BrainConnectionState.Disconnected,
                "Gemini Web is disconnected. The isolated sign-in profile was retained.");
        }
        catch (GeminiWebSessionException exception)
        {
            _status = StatusFor(exception);
            throw ToBrainException(exception);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<BrainResponse> SendAsync(
        BrainRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (request.Messages.Count == 0)
        {
            throw new ArgumentException("At least one brain message is required.", nameof(request));
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_status.State != BrainConnectionState.Connected)
            {
                throw new BrainException(
                    BrainErrorCode.SessionExpired,
                    "Gemini Web is not connected. Reconnect before sending a request.");
            }

            var prompt = GeminiWebPromptFormatter.Format(request);
            var response = await _session.SendPromptAsync(prompt, cancellationToken)
                .ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(response))
            {
                throw new BrainException(
                    BrainErrorCode.InvalidResponse,
                    "Gemini Web returned an empty response.");
            }

            return new BrainResponse.Final(
                Guid.NewGuid().ToString("N"),
                NormalizeProtocolTypography(response));
        }
        catch (GeminiWebSessionException exception)
        {
            _status = StatusFor(exception);
            throw ToBrainException(exception);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            await _session.DisposeAsync().ConfigureAwait(false);
            _disposed = true;
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }

    private BrainStatus ToBrainStatus(GeminiWebSessionStatus status) => status switch
    {
        GeminiWebSessionStatus.Connected => Status(
            BrainConnectionState.Connected,
            "Gemini Web is connected through the isolated Chrome profile."),
        GeminiWebSessionStatus.ReconnectRequired => Status(
            BrainConnectionState.ReconnectRequired,
            "Sign in to Gemini in the opened Chrome window, then reconnect."),
        _ => Status(BrainConnectionState.Disconnected, "Gemini Web is disconnected.")
    };

    private BrainStatus StatusFor(GeminiWebSessionException exception) => exception.Kind switch
    {
        GeminiWebFailureKind.Authentication => Status(
            BrainConnectionState.ReconnectRequired,
            "The Gemini session needs to be reconnected."),
        GeminiWebFailureKind.Compatibility => Status(
            BrainConnectionState.Unavailable,
            "The Gemini Web interface is not compatible with this adapter version."),
        _ => Status(
            BrainConnectionState.Unavailable,
            "Gemini Web is temporarily unavailable.")
    };

    private static BrainException ToBrainException(GeminiWebSessionException exception) =>
        new(
            exception.Kind switch
            {
                GeminiWebFailureKind.Authentication => BrainErrorCode.SessionExpired,
                GeminiWebFailureKind.Compatibility => BrainErrorCode.CompatibilityFailure,
                GeminiWebFailureKind.Network => BrainErrorCode.NetworkFailure,
                _ => BrainErrorCode.Unavailable
            },
            exception.Message,
            exception.IsRetryable,
            exception);

    private static string NormalizeProtocolTypography(string response)
    {
        var trimmed = response.Trim();
        if (trimmed.Length < 2 || trimmed[0] != '{' || trimmed[^1] != '}')
        {
            return response;
        }

        if (IsJsonObject(response))
        {
            return response;
        }

        // Gemini Web occasionally typography-substitutes some or all JSON delimiters even
        // when the model followed the requested envelope. Accept the repaired candidate only
        // when it becomes exactly one JSON object; prose and ambiguous content remain unchanged.
        var candidate = response
            .Replace('\u201c', '"')
            .Replace('\u201d', '"');
        if (IsJsonObject(candidate))
        {
            return candidate;
        }

        candidate = EscapeLineBreaksInsideStrings(candidate);
        return IsJsonObject(candidate) ? candidate : response;
    }

    private static string EscapeLineBreaksInsideStrings(string value)
    {
        var result = new System.Text.StringBuilder(value.Length);
        var insideString = false;
        var escaped = false;
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (!insideString)
            {
                result.Append(character);
                if (character == '"')
                {
                    insideString = true;
                }

                continue;
            }

            if (escaped)
            {
                result.Append(character);
                escaped = false;
                continue;
            }

            if (character == '\\')
            {
                result.Append(character);
                escaped = true;
                continue;
            }

            if (character == '"')
            {
                result.Append(character);
                insideString = false;
                continue;
            }

            if (character == '\r' || character == '\n')
            {
                result.Append("\\n");
                if (character == '\r' && index + 1 < value.Length && value[index + 1] == '\n')
                {
                    index++;
                }

                continue;
            }

            result.Append(character);
        }

        return result.ToString();
    }

    private static bool IsJsonObject(string value)
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(
                value,
                new System.Text.Json.JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = System.Text.Json.JsonCommentHandling.Disallow,
                    MaxDepth = 16
                });
            return document.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object;
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
    }

    private BrainStatus Status(BrainConnectionState state, string message) =>
        new(Provider, state, message);
}
