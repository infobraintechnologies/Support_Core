using CBSSupport.API.Security;
using CBSSupport.API.Tests.TestDoubles;
using Microsoft.Extensions.Logging.Abstractions;

namespace CBSSupport.API.Tests.Security;

public sealed class LoginAttemptLimiterTests
{
    [Fact]
    public async Task Check_SequentialFailuresReachThreshold_BlocksAccountTemporarily()
    {
        var timeProvider = new ManualTimeProvider(new DateTimeOffset(2026, 7, 20, 0, 0, 0, TimeSpan.Zero));
        var limiter = CreateLimiter(timeProvider, failedAttemptsBeforeBackoff: 3);

        await RecordFailedAttemptsAsync(limiter, "account", 3);

        var decision = await limiter.CheckAsync("account", "198.51.100.1");

        Assert.False(decision.IsAllowed);
        Assert.Equal(LoginThrottleBlockReason.AccountBackoff, decision.BlockReason);
        Assert.Equal(TimeSpan.FromMinutes(1), decision.RetryAfter);
    }

    [Fact]
    public async Task Check_TwoLogicalInstancesShareBackoffState()
    {
        var store = new InMemoryLoginThrottleStore();
        var options = CreateOptions(failedAttemptsBeforeBackoff: 2);
        var timeProvider = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var firstInstance = CreateLimiter(store, options, timeProvider);
        var secondInstance = CreateLimiter(store, options, timeProvider);

        await RecordFailedAttemptsAsync(firstInstance, "account", 2);

        var decision = await secondInstance.CheckAsync("account", "198.51.100.1");

        Assert.False(decision.IsAllowed);
        Assert.Equal(LoginThrottleBlockReason.AccountBackoff, decision.BlockReason);
    }

    [Fact]
    public async Task Check_ParallelRequestsAcrossInstances_AtomicallyEnforceSourceWindow()
    {
        var store = new InMemoryLoginThrottleStore();
        var options = CreateOptions(failedAttemptsBeforeBackoff: 100);
        options.SourcePermitLimit = 10;
        var timeProvider = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var firstInstance = CreateLimiter(store, options, timeProvider);
        var secondInstance = CreateLimiter(store, options, timeProvider);

        var decisions = await Task.WhenAll(
            Enumerable.Range(0, 50)
                .Select(index => (index % 2 == 0 ? firstInstance : secondInstance)
                    .CheckAsync("account", "198.51.100.1")));

        Assert.Equal(10, decisions.Count(decision => decision.IsAllowed));
        Assert.Equal(40, decisions.Count(decision => !decision.IsAllowed));
    }

    [Fact]
    public async Task Reset_AfterSuccessfulLogin_ClearsOnlyAccountPairBackoff()
    {
        var store = new InMemoryLoginThrottleStore();
        var options = CreateOptions(failedAttemptsBeforeBackoff: 1);
        var timeProvider = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var limiter = CreateLimiter(store, options, timeProvider);

        await limiter.CheckAsync("account", "198.51.100.1");
        await limiter.RecordFailureAsync("account", "198.51.100.1");
        await limiter.ResetAsync("account", "198.51.100.1");

        var decision = await limiter.CheckAsync("account", "198.51.100.1");

        Assert.True(decision.IsAllowed);
    }

    [Fact]
    public async Task Check_RepeatedBackoffsAreCappedAndCooldownExpires()
    {
        var timeProvider = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var limiter = CreateLimiter(timeProvider, failedAttemptsBeforeBackoff: 1);

        for (var cycle = 0; cycle < 4; cycle++)
        {
            Assert.True((await limiter.CheckAsync("account", "198.51.100.1")).IsAllowed);
            await limiter.RecordFailureAsync("account", "198.51.100.1");
            var decision = await limiter.CheckAsync("account", "198.51.100.1");
            Assert.False(decision.IsAllowed);
            timeProvider.Advance(decision.RetryAfter);
        }

        await limiter.RecordFailureAsync("account", "198.51.100.1");
        var cappedDecision = await limiter.CheckAsync("account", "198.51.100.1");

        Assert.False(cappedDecision.IsAllowed);
        Assert.Equal(TimeSpan.FromMinutes(4), cappedDecision.RetryAfter);
    }

    [Fact]
    public async Task Check_StoreFailure_FailsClosedWithGenericThrottleDecision()
    {
        var store = new InMemoryLoginThrottleStore { ThrowOnReserve = true };
        var limiter = CreateLimiter(store, CreateOptions(3), new ManualTimeProvider(DateTimeOffset.UtcNow));

        var decision = await limiter.CheckAsync("account", "198.51.100.1");

        Assert.False(decision.IsAllowed);
        Assert.Equal(LoginThrottleBlockReason.StoreUnavailable, decision.BlockReason);
        Assert.Equal(TimeSpan.FromSeconds(30), decision.RetryAfter);
    }

    private static LoginAttemptLimiter CreateLimiter(
        TimeProvider timeProvider,
        int failedAttemptsBeforeBackoff) =>
        CreateLimiter(
            new InMemoryLoginThrottleStore(),
            CreateOptions(failedAttemptsBeforeBackoff),
            timeProvider);

    private static LoginAttemptLimiter CreateLimiter(
        InMemoryLoginThrottleStore store,
        LoginSecurityOptions options,
        TimeProvider timeProvider) =>
        new(store, options, timeProvider, NullLogger<LoginAttemptLimiter>.Instance);

    private static LoginSecurityOptions CreateOptions(int failedAttemptsBeforeBackoff) =>
        new()
        {
            SourcePermitLimit = 100,
            SourceWindow = TimeSpan.FromMinutes(1),
            FailedAttemptsBeforeBackoff = failedAttemptsBeforeBackoff,
            InitialBackoff = TimeSpan.FromMinutes(1),
            MaximumBackoff = TimeSpan.FromMinutes(4),
            StateRetention = TimeSpan.FromMinutes(30),
            CleanupBatchSize = 32,
            CleanupEveryOperations = 256
        };

    private static async Task RecordFailedAttemptsAsync(
        ILoginAttemptLimiter limiter,
        string accountKey,
        int count)
    {
        for (var attempt = 0; attempt < count; attempt++)
        {
            Assert.True((await limiter.CheckAsync(accountKey, "198.51.100.1")).IsAllowed);
            await limiter.RecordFailureAsync(accountKey, "198.51.100.1");
        }
    }

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan duration) => _utcNow += duration;
    }
}
