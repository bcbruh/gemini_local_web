using AgentLocalWeb.Agent.Core;

namespace AgentLocalWeb.Agent.Core.Tests;

public sealed class ProtocolV1Tests
{
    private static readonly IReadOnlySet<string> Tools = new HashSet<string>(
        ["read_file", "search_text"],
        StringComparer.Ordinal);

    [Fact]
    public void ParsesExactFinalEnvelope()
    {
        var result = ProtocolV1.Parse(
            """{"protocol":"local-agent/v1","type":"final","message":"done"}""",
            Tools);

        var response = Assert.IsType<ProtocolResponse.Final>(result.Response);
        Assert.True(result.IsSuccess);
        Assert.Null(result.Error);
        Assert.Equal("done", response.Message);
    }

    [Fact]
    public void ParsesSupportedToolRequestAndClonesArguments()
    {
        var result = ProtocolV1.Parse(
            """{"protocol":"local-agent/v1","type":"tool_request","request_id":"call-1","tool":"read_file","arguments":{"path":"src/App.cs","start_line":1}}""",
            Tools);

        var response = Assert.IsType<ProtocolResponse.ToolRequest>(result.Response);
        Assert.Equal("call-1", response.RequestId);
        Assert.Equal("read_file", response.Tool);
        Assert.Equal("src/App.cs", response.Arguments.GetProperty("path").GetString());
    }

    [Theory]
    [InlineData("Please read src/App.cs", ProtocolErrorCode.InvalidJson)]
    [InlineData("[]", ProtocolErrorCode.InvalidShape)]
    [InlineData("{\"protocol\":\"local-agent/v2\",\"type\":\"final\",\"message\":\"done\"}", ProtocolErrorCode.UnsupportedProtocol)]
    [InlineData("{\"protocol\":\"local-agent/v1\",\"type\":\"other\"}", ProtocolErrorCode.UnsupportedType)]
    [InlineData("{\"protocol\":\"local-agent/v1\",\"type\":\"final\",\"message\":\"done\",\"command\":\"rm\"}", ProtocolErrorCode.UnknownField)]
    [InlineData("{\"protocol\":\"local-agent/v1\",\"protocol\":\"local-agent/v1\",\"type\":\"final\",\"message\":\"done\"}", ProtocolErrorCode.DuplicateField)]
    public void RejectsMalformedOrAmbiguousEnvelope(
        string content,
        ProtocolErrorCode expectedCode)
    {
        var result = ProtocolV1.Parse(content, Tools);

        Assert.False(result.IsSuccess);
        Assert.Null(result.Response);
        Assert.Equal(expectedCode, result.Error?.Code);
    }

    [Fact]
    public void RejectsUnknownTool()
    {
        var result = ProtocolV1.Parse(
            """{"protocol":"local-agent/v1","type":"tool_request","request_id":"call-1","tool":"run_command","arguments":{}}""",
            Tools);

        Assert.Equal(ProtocolErrorCode.UnsupportedTool, result.Error?.Code);
    }

    [Fact]
    public void RejectsToolArgumentsThatAreNotAnObject()
    {
        var result = ProtocolV1.Parse(
            """{"protocol":"local-agent/v1","type":"tool_request","request_id":"call-1","tool":"read_file","arguments":"src/App.cs"}""",
            Tools);

        Assert.Equal(ProtocolErrorCode.MissingOrInvalidField, result.Error?.Code);
    }

    [Fact]
    public void RejectsOversizedEnvelopeBeforeParsing()
    {
        var result = ProtocolV1.Parse(
            new string('x', ProtocolV1.MaximumEnvelopeBytes + 1),
            Tools);

        Assert.Equal(ProtocolErrorCode.EnvelopeTooLarge, result.Error?.Code);
    }
}
