namespace CBSSupport.API.Security;

public interface ILoginAttemptLimiter
{
    LoginAttemptDecision Check(string accountKey);

    void RecordFailure(string accountKey);

    void Reset(string accountKey);
}

public readonly record struct LoginAttemptDecision(bool IsAllowed, TimeSpan RetryAfter)
{
    public static LoginAttemptDecision Allowed { get; } = new(true, TimeSpan.Zero);
}
