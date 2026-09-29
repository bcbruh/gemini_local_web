using AgentLocalWeb.AppHost;

namespace AgentLocalWeb.AppHost.Tests;

public sealed class LocalAccessSessionTests
{
    [Fact]
    public void BootstrapTokenIsOneTimeAndCreatesIndependentCredentials()
    {
        var session = new LocalAccessSession(TimeProvider.System);

        var credentials = session.TryBootstrap(session.BootstrapToken);
        var replay = session.TryBootstrap(session.BootstrapToken);

        Assert.NotNull(credentials);
        Assert.Null(replay);
        Assert.NotEqual(credentials.SessionToken, credentials.CsrfToken);
        Assert.True(session.IsAuthenticated(credentials.SessionToken));
        Assert.True(session.IsValidCsrf(credentials.CsrfToken));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-base64!")]
    public void InvalidBootstrapTokenDoesNotCreateSession(string? token)
    {
        var session = new LocalAccessSession(TimeProvider.System);

        Assert.Null(session.TryBootstrap(token));
        Assert.False(session.IsAuthenticated(token));
    }

    [Fact]
    public void ExpiredBootstrapTokenIsRejected()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var session = new LocalAccessSession(time);
        time.Advance(TimeSpan.FromMinutes(3));

        Assert.Null(session.TryBootstrap(session.BootstrapToken));
    }

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;

        public void Advance(TimeSpan duration) => now += duration;
    }
}
