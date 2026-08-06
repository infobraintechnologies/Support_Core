using System.Collections.Concurrent;
using CBSSupport.Shared.Data;

namespace CBSSupport.API.Tests.TestDoubles;

internal sealed class InMemoryLoginThrottleStore : ILoginThrottleStore
{
    private readonly ConcurrentDictionary<string, SourceState> _sources = new();
    private readonly ConcurrentDictionary<string, AccountState> _accounts = new();

    public bool ThrowOnReserve { get; set; }
    public bool ThrowOnRecordFailure { get; set; }
    public bool ThrowOnReset { get; set; }
    public bool ThrowOnCleanup { get; set; }

    public Task<LoginThrottleSnapshot> ReserveAsync(
        string sourceKey,
        string accountPairKey,
        int sourcePermitLimit,
        TimeSpan sourceWindow,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (ThrowOnReserve)
        {
            throw new InvalidOperationException("test store failure");
        }

        var source = _sources.GetOrAdd(sourceKey, _ => new SourceState());
        lock (source.SyncRoot)
        {
            if (source.WindowStartedAt is null || now - source.WindowStartedAt >= sourceWindow)
            {
                source.WindowStartedAt = now;
                source.RequestCount = 1;
            }
            else
            {
                source.RequestCount++;
            }
            source.LastTouchedAt = now;

            var account = _accounts.TryGetValue(accountPairKey, out var existing)
                ? existing
                : null;
            var blockedUntil = account is not null
                ? account.BlockedUntil
                : null;
            return Task.FromResult(new LoginThrottleSnapshot(
                source.RequestCount,
                source.WindowStartedAt.Value + sourceWindow,
                blockedUntil));
        }
    }

    public Task RecordFailureAsync(
        string accountPairKey,
        int failedAttemptsBeforeBackoff,
        TimeSpan initialBackoff,
        TimeSpan maximumBackoff,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (ThrowOnRecordFailure)
        {
            throw new InvalidOperationException("test store failure");
        }

        var account = _accounts.GetOrAdd(accountPairKey, _ => new AccountState());
        lock (account.SyncRoot)
        {
            if (account.BlockedUntil is { } blockedUntil && blockedUntil > now)
            {
                account.LastTouchedAt = now;
                return Task.CompletedTask;
            }

            account.FailedAttempts++;
            account.LastTouchedAt = now;
            if (account.FailedAttempts < failedAttemptsBeforeBackoff)
            {
                return Task.CompletedTask;
            }

            account.FailedAttempts = 0;
            account.BackoffLevel = Math.Min(31, account.BackoffLevel + 1);
            account.BlockedUntil = now + CalculateBackoff(
                initialBackoff,
                maximumBackoff,
                account.BackoffLevel);
        }

        return Task.CompletedTask;
    }

    public Task ResetAsync(
        string accountPairKey,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (ThrowOnReset)
        {
            throw new InvalidOperationException("test store failure");
        }

        _accounts.TryRemove(accountPairKey, out _);
        return Task.CompletedTask;
    }

    public Task CleanupAsync(
        DateTimeOffset olderThan,
        int batchSize,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (ThrowOnCleanup)
        {
            throw new InvalidOperationException("test store failure");
        }

        foreach (var pair in _sources)
        {
            if (pair.Value.LastTouchedAt < olderThan)
            {
                _sources.TryRemove(pair.Key, out _);
            }
        }

        foreach (var pair in _accounts)
        {
            if (pair.Value.LastTouchedAt < olderThan)
            {
                _accounts.TryRemove(pair.Key, out _);
            }
        }

        return Task.CompletedTask;
    }

    private static TimeSpan CalculateBackoff(
        TimeSpan initialBackoff,
        TimeSpan maximumBackoff,
        int level)
    {
        var backoff = initialBackoff;
        for (var currentLevel = 1; currentLevel < level && backoff < maximumBackoff; currentLevel++)
        {
            backoff = backoff > maximumBackoff / 2
                ? maximumBackoff
                : backoff * 2;
        }

        return backoff > maximumBackoff ? maximumBackoff : backoff;
    }

    private sealed class SourceState
    {
        public object SyncRoot { get; } = new();
        public DateTimeOffset? WindowStartedAt { get; set; }
        public int RequestCount { get; set; }
        public DateTimeOffset LastTouchedAt { get; set; } = DateTimeOffset.UtcNow;
    }

    private sealed class AccountState
    {
        public object SyncRoot { get; } = new();
        public int FailedAttempts { get; set; }
        public int BackoffLevel { get; set; }
        public DateTimeOffset? BlockedUntil { get; set; }
        public DateTimeOffset LastTouchedAt { get; set; } = DateTimeOffset.UtcNow;
    }
}
