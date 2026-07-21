using CBSSupport.API.Security;

namespace CBSSupport.API.Tests.Security;

public sealed class LoginAttemptLimiterTests
{
    [Fact]
    public void Check_SequentialFailuresReachThreshold_BlocksAccountTemporarily()
    {
        var timeProvider = new ManualTimeProvider(new DateTimeOffset(2026, 7, 20, 0, 0, 0, TimeSpan.Zero));
        var limiter = CreateLimiter(timeProvider, failedAttemptsBeforeBackoff: 3);

        RecordFailedAttempts(limiter, "account", 3);

        var decision = limiter.Check("account");

        Assert.False(decision.IsAllowed);
        Assert.Equal(TimeSpan.FromMinutes(1), decision.RetryAfter);
    }

    [Fact]
    public void Check_ExpiredBackoff_AllowsNewAttemptsAndIncreasesNextBackoff()
    {
        var timeProvider = new ManualTimeProvider(new DateTimeOffset(2026, 7, 20, 0, 0, 0, TimeSpan.Zero));
        var limiter = CreateLimiter(timeProvider, failedAttemptsBeforeBackoff: 2);
        RecordFailedAttempts(limiter, "account", 2);
        timeProvider.Advance(TimeSpan.FromMinutes(1));

        Assert.True(limiter.Check("account").IsAllowed);
        RecordFailedAttempts(limiter, "account", 2);

        var decision = limiter.Check("account");

        Assert.False(decision.IsAllowed);
        Assert.Equal(TimeSpan.FromMinutes(2), decision.RetryAfter);
    }

    [Fact]
    public void Check_RepeatedBackoffs_DoNotExceedConfiguredMaximum()
    {
        var timeProvider = new ManualTimeProvider(new DateTimeOffset(2026, 7, 20, 0, 0, 0, TimeSpan.Zero));
        var limiter = CreateLimiter(timeProvider, failedAttemptsBeforeBackoff: 1);

        for (var cycle = 0; cycle < 4; cycle++)
        {
            Assert.True(limiter.Check("account").IsAllowed);
            limiter.RecordFailure("account");
            var decision = limiter.Check("account");
            Assert.False(decision.IsAllowed);
            timeProvider.Advance(decision.RetryAfter);
        }

        limiter.RecordFailure("account");
        var cappedDecision = limiter.Check("account");

        Assert.False(cappedDecision.IsAllowed);
        Assert.Equal(TimeSpan.FromMinutes(4), cappedDecision.RetryAfter);
    }

    [Fact]
    public void Reset_BlockedAccount_RemovesBackoffHistory()
    {
        var timeProvider = new ManualTimeProvider(new DateTimeOffset(2026, 7, 20, 0, 0, 0, TimeSpan.Zero));
        var limiter = CreateLimiter(timeProvider, failedAttemptsBeforeBackoff: 1);
        limiter.RecordFailure("account");

        limiter.Reset("account");

        Assert.True(limiter.Check("account").IsAllowed);
        limiter.RecordFailure("account");
        Assert.Equal(TimeSpan.FromMinutes(1), limiter.Check("account").RetryAfter);
    }

    [Fact]
    public void Check_DifferentAccountKey_IsNotAffectedByBlockedAccount()
    {
        var timeProvider = new ManualTimeProvider(new DateTimeOffset(2026, 7, 20, 0, 0, 0, TimeSpan.Zero));
        var limiter = CreateLimiter(timeProvider, failedAttemptsBeforeBackoff: 1);
        limiter.RecordFailure("blocked-account");

        Assert.False(limiter.Check("blocked-account").IsAllowed);
        Assert.True(limiter.Check("other-account").IsAllowed);
    }

    private static LoginAttemptLimiter CreateLimiter(
        TimeProvider timeProvider,
        int failedAttemptsBeforeBackoff) =>
        new(
            new LoginSecurityOptions
            {
                FailedAttemptsBeforeBackoff = failedAttemptsBeforeBackoff,
                InitialBackoff = TimeSpan.FromMinutes(1),
                MaximumBackoff = TimeSpan.FromMinutes(4),
                StateRetention = TimeSpan.FromHours(1),
                MaximumTrackedAccounts = 100
            },
            timeProvider);

    private static void RecordFailedAttempts(
        ILoginAttemptLimiter limiter,
        string accountKey,
        int count)
    {
        for (var attempt = 0; attempt < count; attempt++)
        {
            Assert.True(limiter.Check(accountKey).IsAllowed);
            limiter.RecordFailure(accountKey);
        }
    }

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan duration) => _utcNow += duration;
    }
}
