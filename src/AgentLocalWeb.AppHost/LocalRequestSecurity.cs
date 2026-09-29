namespace AgentLocalWeb.AppHost;

internal static class LocalRequestSecurity
{
    public static bool HasAllowedHost(HttpRequest request) =>
        request.Host.Host.Equals("127.0.0.1", StringComparison.Ordinal);

    public static bool HasAllowedOrigin(HttpRequest request)
    {
        if (!request.Headers.TryGetValue("Origin", out var values))
        {
            return true;
        }

        if (values.Count != 1 ||
            !Uri.TryCreate(values[0], UriKind.Absolute, out var origin))
        {
            return false;
        }

        return origin.Scheme == Uri.UriSchemeHttp &&
            origin.Host.Equals("127.0.0.1", StringComparison.Ordinal) &&
            origin.Port == request.Host.Port;
    }

    public static bool RequiresCsrf(HttpRequest request) =>
        !HttpMethods.IsGet(request.Method) &&
        !HttpMethods.IsHead(request.Method) &&
        !HttpMethods.IsOptions(request.Method);
}
