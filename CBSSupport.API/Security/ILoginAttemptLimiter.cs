namespace CBSSupport.API.Security;

public interface ILoginAttemptLimiter
{
    Task<LoginAttemptDecision> CheckAsync(
        string accountKey,
        string clientSignal,
        CancellationToken cancellationToken = default);

    Task RecordFailureAsync(
        string accountKey,
        string clientSignal,
        CancellationToken cancellationToken = default);

    Task ResetAsync(
        string accountKey,
        string clientSignal,
        CancellationToken cancellationToken = default);
}

public enum LoginThrottleBlockReason
{
    None,
    SourceLimit,
    AccountBackoff,
    StoreUnavailable
}

public readonly record struct LoginAttemptDecision(
    bool IsAllowed,
    TimeSpan RetryAfter,
    LoginThrottleBlockReason BlockReason = LoginThrottleBlockReason.None)
{
    public static LoginAttemptDecision Allowed { get; } =
        new(true, TimeSpan.Zero, LoginThrottleBlockReason.None);
}
