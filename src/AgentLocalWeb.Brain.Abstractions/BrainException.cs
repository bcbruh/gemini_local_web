namespace AgentLocalWeb.Brain;

public sealed class BrainException : Exception
{
    public BrainException(
        BrainErrorCode code,
        string message,
        bool isRetryable = false,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
        IsRetryable = isRetryable;
    }

    public BrainErrorCode Code { get; }

    public bool IsRetryable { get; }
}
