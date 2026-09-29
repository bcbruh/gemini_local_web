namespace AgentLocalWeb.Brain;

public enum BrainErrorCode
{
    AuthenticationFailed,
    SessionExpired,
    RateLimited,
    NetworkFailure,
    CompatibilityFailure,
    InvalidResponse,
    Unavailable
}
