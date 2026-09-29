using System.Text;
using System.Text.Json;

namespace AgentLocalWeb.Agent.Core;

public static class ProtocolV1
{
    public const string Version = "local-agent/v1";
    public const int MaximumEnvelopeBytes = 64 * 1024;

    private static readonly HashSet<string> FinalFields =
        ["protocol", "type", "message"];

    private static readonly HashSet<string> ToolRequestFields =
        ["protocol", "type", "request_id", "tool", "arguments"];

    public static ProtocolParseResult Parse(
        string content,
        IReadOnlySet<string> supportedTools)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(supportedTools);

        if (Encoding.UTF8.GetByteCount(content) > MaximumEnvelopeBytes)
        {
            return ProtocolParseResult.Failure(
                ProtocolErrorCode.EnvelopeTooLarge,
                "The protocol envelope exceeds the 64 KiB limit.");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(content, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 16
            });
        }
        catch (JsonException)
        {
            return ProtocolParseResult.Failure(
                ProtocolErrorCode.InvalidJson,
                "The brain response is not valid protocol JSON.");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return ProtocolParseResult.Failure(
                    ProtocolErrorCode.InvalidShape,
                    "The protocol envelope must be a JSON object.");
            }

            var fields = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
            {
                if (!fields.Add(property.Name))
                {
                    return ProtocolParseResult.Failure(
                        ProtocolErrorCode.DuplicateField,
                        $"The protocol envelope contains duplicate field '{property.Name}'.");
                }
            }

            if (!TryReadRequiredString(root, "protocol", out var protocol) ||
                !string.Equals(protocol, Version, StringComparison.Ordinal))
            {
                return ProtocolParseResult.Failure(
                    ProtocolErrorCode.UnsupportedProtocol,
                    $"The protocol must be '{Version}'.");
            }

            if (!TryReadRequiredString(root, "type", out var type))
            {
                return ProtocolParseResult.Failure(
                    ProtocolErrorCode.MissingOrInvalidField,
                    "The protocol type is required.");
            }

            return type switch
            {
                "final" => ParseFinal(root, fields),
                "tool_request" => ParseToolRequest(root, fields, supportedTools),
                _ => ProtocolParseResult.Failure(
                    ProtocolErrorCode.UnsupportedType,
                    "The protocol response type is not supported.")
            };
        }
    }

    private static ProtocolParseResult ParseFinal(
        JsonElement root,
        HashSet<string> fields)
    {
        var unexpected = fields.FirstOrDefault(field => !FinalFields.Contains(field));
        if (unexpected is not null)
        {
            return UnknownField(unexpected);
        }

        if (!HasExactly(fields, FinalFields) ||
            !TryReadRequiredString(root, "message", out var message))
        {
            return ProtocolParseResult.Failure(
                ProtocolErrorCode.MissingOrInvalidField,
                "A final response requires a non-empty message.");
        }

        return ProtocolParseResult.Success(new ProtocolResponse.Final(message));
    }

    private static ProtocolParseResult ParseToolRequest(
        JsonElement root,
        HashSet<string> fields,
        IReadOnlySet<string> supportedTools)
    {
        var unexpected = fields.FirstOrDefault(field => !ToolRequestFields.Contains(field));
        if (unexpected is not null)
        {
            return UnknownField(unexpected);
        }

        if (!HasExactly(fields, ToolRequestFields) ||
            !TryReadRequiredString(root, "request_id", out var requestId) ||
            !TryReadRequiredString(root, "tool", out var tool) ||
            !root.TryGetProperty("arguments", out var arguments) ||
            arguments.ValueKind != JsonValueKind.Object)
        {
            return ProtocolParseResult.Failure(
                ProtocolErrorCode.MissingOrInvalidField,
                "A tool request requires request_id, tool and an arguments object.");
        }

        if (requestId.Length > 100 || tool.Length > 100)
        {
            return ProtocolParseResult.Failure(
                ProtocolErrorCode.MissingOrInvalidField,
                "Tool request identifiers exceed their size limit.");
        }

        if (!supportedTools.Contains(tool))
        {
            return ProtocolParseResult.Failure(
                ProtocolErrorCode.UnsupportedTool,
                $"Tool '{tool}' is not available.");
        }

        return ProtocolParseResult.Success(
            new ProtocolResponse.ToolRequest(requestId, tool, arguments.Clone()));
    }

    private static bool TryReadRequiredString(
        JsonElement root,
        string name,
        out string value)
    {
        if (root.TryGetProperty(name, out var property) &&
            property.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(property.GetString()))
        {
            value = property.GetString()!;
            return true;
        }

        value = string.Empty;
        return false;
    }

    private static bool HasExactly(HashSet<string> actual, HashSet<string> expected) =>
        actual.Count == expected.Count && actual.SetEquals(expected);

    private static ProtocolParseResult UnknownField(string field) =>
        ProtocolParseResult.Failure(
            ProtocolErrorCode.UnknownField,
            $"Field '{field}' is not allowed for this response type.");
}
