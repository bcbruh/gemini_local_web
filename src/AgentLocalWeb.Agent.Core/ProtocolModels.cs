using System.Text.Json;

namespace AgentLocalWeb.Agent.Core;

public abstract record ProtocolResponse
{
    private ProtocolResponse()
    {
    }

    public sealed record Final(string Message) : ProtocolResponse;

    public sealed record ToolRequest(
        string RequestId,
        string Tool,
        JsonElement Arguments) : ProtocolResponse;
}

public enum ProtocolErrorCode
{
    InvalidJson,
    EnvelopeTooLarge,
    InvalidShape,
    DuplicateField,
    MissingOrInvalidField,
    UnknownField,
    UnsupportedProtocol,
    UnsupportedType,
    UnsupportedTool
}

public sealed record ProtocolParseError(
    ProtocolErrorCode Code,
    string Message);

public sealed record ProtocolParseResult
{
    private ProtocolParseResult(
        ProtocolResponse? response,
        ProtocolParseError? error)
    {
        Response = response;
        Error = error;
    }

    public ProtocolResponse? Response { get; }

    public ProtocolParseError? Error { get; }

    public bool IsSuccess => Response is not null;

    internal static ProtocolParseResult Success(ProtocolResponse response) =>
        new(response, null);

    internal static ProtocolParseResult Failure(ProtocolErrorCode code, string message) =>
        new(null, new ProtocolParseError(code, message));
}
