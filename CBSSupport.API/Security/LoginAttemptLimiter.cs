using System.Collections.Concurrent;

namespace CBSSupport.API.Security;

public sealed class LoginAttemptLimiter : ILoginAttemptLimiter
{
    private const int CleanupInterval = 256;

    private readonly ConcurrentDictionary<string, AttemptState> _states = new();
    private readonly ConcurrentQueue<KeyValuePair<string, AttemptState>> _insertionOrder = new();
    private readonly object _stateCreationLock = new();
    private readonly LoginSecurityOptions _options;
    private readonly TimeProvider _timeProvider;
    private long _operationCount;

    public LoginAttemptLimiter(LoginSecurityOptions options, TimeProvider timeProvider)
    {
        _options = options;
        _timeProvider = timeProvider;
    }

    public LoginAttemptDecision Check(string accountKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountKey);

        var now = _timeProvider.GetUtcNow();
        CleanupIfNeeded(now);

        if (!_states.TryGetValue(accountKey, out var state))
        {
            return LoginAttemptDecision.Allowed;
        }

        lock (state.SyncRoot)
        {
            state.LastTouched = now;

            if (state.BlockedUntil is not { } blockedUntil)
            {
                return LoginAttemptDecision.Allowed;
            }

            if (blockedUntil > now)
            {
                return new LoginAttemptDecision(false, blockedUntil - now);
            }

            state.BlockedUntil = null;
            return LoginAttemptDecision.Allowed;
        }
    }

    public void RecordFailure(string accountKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountKey);

        var now = _timeProvider.GetUtcNow();
        CleanupIfNeeded(now);

        var state = GetOrCreateState(accountKey, now);

        lock (state.SyncRoot)
        {
            state.LastTouched = now;

            if (state.BlockedUntil is { } blockedUntil && blockedUntil > now)
            {
                return;
            }

            state.FailedAttempts++;
            if (state.FailedAttempts < _options.FailedAttemptsBeforeBackoff)
            {
                return;
            }

            state.FailedAttempts = 0;
            if (state.BackoffLevel < 31)
            {
                state.BackoffLevel++;
            }

            state.BlockedUntil = now + CalculateBackoff(state.BackoffLevel);
        }
    }

    public void Reset(string accountKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountKey);
        _states.TryRemove(accountKey, out _);
    }

    private TimeSpan CalculateBackoff(int backoffLevel)
    {
        var backoff = _options.InitialBackoff;
        for (var level = 1; level < backoffLevel && backoff < _options.MaximumBackoff; level++)
        {
            backoff = backoff > _options.MaximumBackoff / 2
                ? _options.MaximumBackoff
                : backoff * 2;
        }

        return backoff > _options.MaximumBackoff
            ? _options.MaximumBackoff
            : backoff;
    }

    private AttemptState GetOrCreateState(string accountKey, DateTimeOffset now)
    {
        if (_states.TryGetValue(accountKey, out var existingState))
        {
            return existingState;
        }

        lock (_stateCreationLock)
        {
            if (_states.TryGetValue(accountKey, out existingState))
            {
                return existingState;
            }

            TrimToCapacity();

            var newState = new AttemptState(now);
            _states[accountKey] = newState;
            _insertionOrder.Enqueue(new KeyValuePair<string, AttemptState>(accountKey, newState));
            return newState;
        }
    }

    private void CleanupIfNeeded(DateTimeOffset now)
    {
        if (Interlocked.Increment(ref _operationCount) % CleanupInterval != 0)
        {
            return;
        }

        foreach (var pair in _states)
        {
            var state = pair.Value;
            lock (state.SyncRoot)
            {
                if (now - state.LastTouched >= _options.StateRetention
                    && (state.BlockedUntil is null || state.BlockedUntil <= now))
                {
                    _states.TryRemove(pair);
                }
            }
        }
    }

    private void TrimToCapacity()
    {
        while (_states.Count >= _options.MaximumTrackedAccounts
               && _insertionOrder.TryDequeue(out var oldestState))
        {
            _states.TryRemove(oldestState);
        }
    }

    private sealed class AttemptState(DateTimeOffset now)
    {
        public object SyncRoot { get; } = new();
        public int FailedAttempts { get; set; }
        public int BackoffLevel { get; set; }
        public DateTimeOffset? BlockedUntil { get; set; }
        public DateTimeOffset LastTouched { get; set; } = now;
    }
}
