using AgentLocalWeb.AppHost;
using Microsoft.AspNetCore.Http;

namespace AgentLocalWeb.AppHost.Tests;

public sealed class LocalRequestSecurityTests
{
    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("localhost", false)]
    [InlineData("example.com", false)]
    public void AllowsOnlyExactIpv4LoopbackHost(string host, bool expected)
    {
        var context = new DefaultHttpContext();
        context.Request.Host = new HostString(host, 51234);

        Assert.Equal(expected, LocalRequestSecurity.HasAllowedHost(context.Request));
    }

    [Theory]
    [InlineData("http://127.0.0.1:51234", true)]
    [InlineData("http://127.0.0.1:9999", false)]
    [InlineData("https://127.0.0.1:51234", false)]
    [InlineData("https://example.com", false)]
    public void OriginMustMatchLoopbackSchemeHostAndPort(string origin, bool expected)
    {
        var context = new DefaultHttpContext();
        context.Request.Host = new HostString("127.0.0.1", 51234);
        context.Request.Headers.Origin = origin;

        Assert.Equal(expected, LocalRequestSecurity.HasAllowedOrigin(context.Request));
    }

    [Fact]
    public void MissingOriginIsAllowedForNonBrowserLocalClients()
    {
        var context = new DefaultHttpContext();
        context.Request.Host = new HostString("127.0.0.1", 51234);

        Assert.True(LocalRequestSecurity.HasAllowedOrigin(context.Request));
    }
}
