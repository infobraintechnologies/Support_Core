using Dapper;
using Npgsql;

namespace CBSSupport.Shared.Data;

public readonly record struct LoginThrottleSnapshot(
    int SourceRequestCount,
    DateTimeOffset SourceWindowEnds,
    DateTimeOffset? AccountBlockedUntil);

public interface ILoginThrottleStore
{
    Task<LoginThrottleSnapshot> ReserveAsync(
        string sourceKey,
        string accountPairKey,
        int sourcePermitLimit,
        TimeSpan sourceWindow,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    Task RecordFailureAsync(
        string accountPairKey,
        int failedAttemptsBeforeBackoff,
        TimeSpan initialBackoff,
        TimeSpan maximumBackoff,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    Task ResetAsync(
        string accountPairKey,
        CancellationToken cancellationToken = default);

    Task CleanupAsync(
        DateTimeOffset olderThan,
        int batchSize,
        CancellationToken cancellationToken = default);
}

public sealed class LoginThrottleStore(string connectionString) : ILoginThrottleStore
{
    public async Task<LoginThrottleSnapshot> ReserveAsync(
        string sourceKey,
        string accountPairKey,
        int sourcePermitLimit,
        TimeSpan sourceWindow,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ValidateKey(sourceKey, nameof(sourceKey));
        ValidateKey(accountPairKey, nameof(accountPairKey));
        if (sourcePermitLimit <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sourcePermitLimit));
        }

        if (sourceWindow <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceWindow));
        }

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var windowStartBoundary = now - sourceWindow;
        var source = await connection.QuerySingleAsync<SourceBucket>(
            new CommandDefinition(
                """
                INSERT INTO digital.login_throttle_buckets AS bucket (
                    bucket_kind,
                    bucket_key,
                    window_started_at,
                    request_count,
                    failed_attempts,
                    backoff_level,
                    blocked_until,
                    last_touched_at)
                VALUES (
                    'source',
                    @SourceKey,
                    @Now,
                    1,
                    0,
                    0,
                    NULL,
                    @Now)
                ON CONFLICT (bucket_kind, bucket_key) DO UPDATE
                SET window_started_at = CASE
                        WHEN bucket.window_started_at <= @WindowStartBoundary
                            THEN @Now
                        ELSE bucket.window_started_at
                    END,
                    request_count = CASE
                        WHEN bucket.window_started_at <= @WindowStartBoundary
                            THEN 1
                        ELSE LEAST(bucket.request_count + 1, @SourceCountCap)
                    END,
                    last_touched_at = @Now
                RETURNING request_count AS RequestCount,
                          window_started_at AS WindowStartedAt;
                """,
                new
                {
                    SourceKey = sourceKey,
                    Now = now,
                    WindowStartBoundary = windowStartBoundary,
                    SourceCountCap = sourcePermitLimit == int.MaxValue
                        ? int.MaxValue
                        : sourcePermitLimit + 1
                },
                transaction,
                cancellationToken: cancellationToken));

        var accountBlockedUntil = await connection.QuerySingleOrDefaultAsync<DateTime?>(
            new CommandDefinition(
                """
                SELECT blocked_until
                FROM digital.login_throttle_buckets
                WHERE bucket_kind = 'account'
                  AND bucket_key = @AccountPairKey;
                """,
                new { AccountPairKey = accountPairKey },
                transaction,
                cancellationToken: cancellationToken));

        await transaction.CommitAsync(cancellationToken);
        return new LoginThrottleSnapshot(
            source.RequestCount,
            ToUtc(source.WindowStartedAt) + sourceWindow,
            accountBlockedUntil is null ? null : ToUtc(accountBlockedUntil.Value));
    }

    public async Task RecordFailureAsync(
        string accountPairKey,
        int failedAttemptsBeforeBackoff,
        TimeSpan initialBackoff,
        TimeSpan maximumBackoff,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ValidateKey(accountPairKey, nameof(accountPairKey));
        if (failedAttemptsBeforeBackoff <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(failedAttemptsBeforeBackoff));
        }

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var failure = await connection.QuerySingleAsync<FailureBucket>(
            new CommandDefinition(
                """
                INSERT INTO digital.login_throttle_buckets AS bucket (
                    bucket_kind,
                    bucket_key,
                    window_started_at,
                    request_count,
                    failed_attempts,
                    backoff_level,
                    blocked_until,
                    last_touched_at)
                VALUES (
                    'account',
                    @AccountPairKey,
                    @Now,
                    0,
                    1,
                    0,
                    NULL,
                    @Now)
                ON CONFLICT (bucket_kind, bucket_key) DO UPDATE
                SET failed_attempts = CASE
                        WHEN bucket.blocked_until IS NOT NULL
                             AND bucket.blocked_until > @Now
                            THEN bucket.failed_attempts
                        ELSE bucket.failed_attempts + 1
                    END,
                    last_touched_at = @Now
                RETURNING failed_attempts AS FailedAttempts,
                          backoff_level AS BackoffLevel,
                          blocked_until AS BlockedUntil;
                """,
                new { AccountPairKey = accountPairKey, Now = now },
                transaction,
                cancellationToken: cancellationToken));

        var failureBlockedUntil = failure.BlockedUntil is null
            ? (DateTimeOffset?)null
            : ToUtc(failure.BlockedUntil.Value);

        if (failureBlockedUntil is not null && failureBlockedUntil > now
            || failure.FailedAttempts < failedAttemptsBeforeBackoff)
        {
            await transaction.CommitAsync(cancellationToken);
            return;
        }

        var newBackoffLevel = Math.Min(31, failure.BackoffLevel + 1);
        var blockedUntil = now + CalculateBackoff(
            initialBackoff,
            maximumBackoff,
            newBackoffLevel);
        await connection.ExecuteAsync(
            new CommandDefinition(
                """
                UPDATE digital.login_throttle_buckets
                SET failed_attempts = 0,
                    backoff_level = @BackoffLevel,
                    blocked_until = @BlockedUntil,
                    last_touched_at = @Now
                WHERE bucket_kind = 'account'
                  AND bucket_key = @AccountPairKey;
                """,
                new
                {
                    AccountPairKey = accountPairKey,
                    BackoffLevel = newBackoffLevel,
                    BlockedUntil = blockedUntil,
                    Now = now
                },
                transaction,
                cancellationToken: cancellationToken));

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task ResetAsync(
        string accountPairKey,
        CancellationToken cancellationToken = default)
    {
        ValidateKey(accountPairKey, nameof(accountPairKey));
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await connection.ExecuteAsync(
            new CommandDefinition(
                """
                DELETE FROM digital.login_throttle_buckets
                WHERE bucket_kind = 'account'
                  AND bucket_key = @AccountPairKey;
                """,
                new { AccountPairKey = accountPairKey },
                cancellationToken: cancellationToken));
    }

    public async Task CleanupAsync(
        DateTimeOffset olderThan,
        int batchSize,
        CancellationToken cancellationToken = default)
    {
        if (batchSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(batchSize));
        }

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await connection.ExecuteAsync(
            new CommandDefinition(
                """
                WITH stale AS (
                    SELECT bucket_kind, bucket_key
                    FROM digital.login_throttle_buckets
                    WHERE last_touched_at < @OlderThan
                    ORDER BY last_touched_at
                    LIMIT @BatchSize
                )
                DELETE FROM digital.login_throttle_buckets AS bucket
                USING stale
                WHERE bucket.bucket_kind = stale.bucket_kind
                  AND bucket.bucket_key = stale.bucket_key;
                """,
                new { OlderThan = olderThan, BatchSize = batchSize },
                cancellationToken: cancellationToken));
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

    private static void ValidateKey(string key, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key, parameterName);
        if (key.Length != 64 || key.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException("Throttle keys must be SHA-256 hex values.", parameterName);
        }
    }

    private static DateTimeOffset ToUtc(DateTime value) =>
        new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    private sealed class SourceBucket
    {
        public int RequestCount { get; set; }

        public DateTime WindowStartedAt { get; set; }
    }

    private sealed class FailureBucket
    {
        public int FailedAttempts { get; set; }

        public int BackoffLevel { get; set; }

        public DateTime? BlockedUntil { get; set; }
    }
}
