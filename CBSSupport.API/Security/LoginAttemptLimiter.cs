using CBSSupport.Shared.Data;

namespace CBSSupport.API.Security;

public sealed class LoginAttemptLimiter(
    ILoginThrottleStore store,
    LoginSecurityOptions options,
    TimeProvider timeProvider,
    ILogger<LoginAttemptLimiter> logger) : ILoginAttemptLimiter
{
    private long _operationCount;

    public async Task<LoginAttemptDecision> CheckAsync(
        string accountKey,
        string clientSignal,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientSignal);
        var pairKey = LoginAccountKey.Pair(accountKey, clientSignal);
        var sourceKey = LoginAccountKey.Source(clientSignal);
        var now = timeProvider.GetUtcNow();

        try
        {
            var snapshot = await store.ReserveAsync(
                sourceKey,
                pairKey,
                options.SourcePermitLimit,
                options.SourceWindow,
                now,
                cancellationToken);

            var decision = CreateDecision(snapshot, now, options.SourcePermitLimit);
            LoginThrottleMetrics.Checks.Add(1);
            if (!decision.IsAllowed)
            {
                LoginThrottleMetrics.Blocked.Add(1,
                    new KeyValuePair<string, object?>("reason", decision.BlockReason.ToString()));
                logger.LogWarning(
                    "Login attempt throttled because of {Reason}; retry after {RetryAfterSeconds} seconds",
                    decision.BlockReason,
                    Math.Max(1, (int)Math.Ceiling(decision.RetryAfter.TotalSeconds)));
            }

            await CleanupIfDueAsync(now, cancellationToken);
            return decision;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            LoginThrottleMetrics.StoreFailures.Add(1,
                new KeyValuePair<string, object?>("operation", "check"));
            logger.LogError(
                exception,
                "Distributed login throttle store unavailable; rejecting the login attempt");
            return new LoginAttemptDecision(
                false,
                options.StoreFailureRetryAfter,
                LoginThrottleBlockReason.StoreUnavailable);
        }
    }

    public async Task RecordFailureAsync(
        string accountKey,
        string clientSignal,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientSignal);
        var pairKey = LoginAccountKey.Pair(accountKey, clientSignal);
        try
        {
            await store.RecordFailureAsync(
                pairKey,
                options.FailedAttemptsBeforeBackoff,
                options.InitialBackoff,
                options.MaximumBackoff,
                timeProvider.GetUtcNow(),
                cancellationToken);
            LoginThrottleMetrics.Failures.Add(1);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            LoginThrottleMetrics.StoreFailures.Add(1,
                new KeyValuePair<string, object?>("operation", "record-failure"));
            logger.LogError(
                exception,
                "Distributed login throttle store unavailable while recording a failed login");
        }
    }

    public async Task ResetAsync(
        string accountKey,
        string clientSignal,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientSignal);
        var pairKey = LoginAccountKey.Pair(accountKey, clientSignal);
        try
        {
            await store.ResetAsync(pairKey, cancellationToken);
            LoginThrottleMetrics.Resets.Add(1);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            LoginThrottleMetrics.StoreFailures.Add(1,
                new KeyValuePair<string, object?>("operation", "reset"));
            logger.LogError(
                exception,
                "Distributed login throttle store unavailable while resetting a successful login");
        }
    }

    private async Task CleanupIfDueAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (Interlocked.Increment(ref _operationCount) % options.CleanupEveryOperations != 0)
        {
            return;
        }

        try
        {
            await store.CleanupAsync(
                now - options.StateRetention,
                options.CleanupBatchSize,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            LoginThrottleMetrics.StoreFailures.Add(1,
                new KeyValuePair<string, object?>("operation", "cleanup"));
            logger.LogWarning(
                exception,
                "Distributed login throttle cleanup failed; login throttling remains active");
        }
    }

    private static LoginAttemptDecision CreateDecision(
        LoginThrottleSnapshot snapshot,
        DateTimeOffset now,
        int sourcePermitLimit)
    {
        var sourceBlocked = snapshot.SourceRequestCount > sourcePermitLimit
            && snapshot.SourceWindowEnds > now;
        var accountUntil = snapshot.AccountBlockedUntil;
        var accountBlocked = accountUntil is { } && accountUntil > now;

        if (sourceBlocked && accountBlocked)
        {
            var blockedUntil = accountUntil.GetValueOrDefault();
            return snapshot.SourceWindowEnds >= blockedUntil
                ? new LoginAttemptDecision(
                    false,
                    snapshot.SourceWindowEnds - now,
                    LoginThrottleBlockReason.SourceLimit)
                : new LoginAttemptDecision(
                    false,
                    blockedUntil - now,
                    LoginThrottleBlockReason.AccountBackoff);
        }

        if (sourceBlocked)
        {
            return new LoginAttemptDecision(
                false,
                snapshot.SourceWindowEnds - now,
                LoginThrottleBlockReason.SourceLimit);
        }

        if (accountBlocked)
        {
            return new LoginAttemptDecision(
                false,
                accountUntil.GetValueOrDefault() - now,
                LoginThrottleBlockReason.AccountBackoff);
        }

        return LoginAttemptDecision.Allowed;
    }
}
