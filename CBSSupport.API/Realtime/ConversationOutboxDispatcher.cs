using CBSSupport.Shared.Services;
using Microsoft.Extensions.Options;

namespace CBSSupport.API.Realtime;

public sealed class ConversationOutboxDispatcher(
    IConversationOutboxRepository outbox,
    IConversationRealtimePublisher publisher,
    IOptions<ConversationOutboxDispatcherOptions> options,
    TimeProvider timeProvider,
    ILogger<ConversationOutboxDispatcher> logger) : BackgroundService
{
    private readonly ConversationOutboxDispatcherOptions _options = options.Value;
    private readonly string _leaseOwner = $"{Environment.ProcessId}:{Guid.NewGuid():N}";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var foundWork = await DispatchBatchAsync(stoppingToken);
            if (!foundWork)
            {
                await Task.Delay(_options.PollInterval, timeProvider, stoppingToken);
            }
        }
    }

    internal async Task<bool> DispatchBatchAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<Shared.Contracts.ConversationOutboxItem> items;
        var now = timeProvider.GetUtcNow().UtcDateTime;
        try
        {
            items = await outbox.ClaimAsync(
                _leaseOwner,
                Math.Clamp(_options.BatchSize, 1, 100),
                now,
                now.Add(_options.LeaseDuration),
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Conversation outbox claim failed");
            return false;
        }

        foreach (var item in items)
        {
            try
            {
                if (item.AccessVersion != item.CurrentVersion
                    || !string.Equals(
                        item.ConversationState,
                        item.CurrentState,
                        StringComparison.Ordinal))
                {
                    // A transfer/archive committed after this event. Suppress the stale
                    // audience snapshot; HTTP reconciliation remains authoritative.
                    logger.LogInformation(
                        "Conversation outbox event {EventId} was superseded by access version {CurrentVersion}",
                        item.EventId,
                        item.CurrentVersion);
                    await outbox.MarkProcessedAsync(
                        item.EventId,
                        _leaseOwner,
                        timeProvider.GetUtcNow().UtcDateTime,
                        cancellationToken);
                    continue;
                }


                await using var deliveryLease = await outbox.AcquireDeliveryLeaseAsync(
                    item.ConversationId,
                    item.ConversationState,
                    item.AccessVersion,
                    cancellationToken);
                if (deliveryLease is null)
                {
                    logger.LogInformation(
                        "Conversation outbox event {EventId} was superseded before publication",
                        item.EventId);
                    await outbox.MarkProcessedAsync(
                        item.EventId,
                        _leaseOwner,
                        timeProvider.GetUtcNow().UtcDateTime,
                        cancellationToken);
                    continue;
                }

                await publisher.PublishAsync(item, cancellationToken);
                await outbox.MarkProcessedAsync(
                    item.EventId,
                    _leaseOwner,
                    timeProvider.GetUtcNow().UtcDateTime,
                    cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                var deadLetter = item.AttemptCount >= Math.Max(_options.MaxAttempts, 1);
                var retryDelay = GetRetryDelay(item.AttemptCount);

                logger.LogError(
                    exception,
                    "Conversation outbox event {EventId} publish failed on attempt {AttemptCount}",
                    item.EventId,
                    item.AttemptCount);
                if (deadLetter)
                {
                    logger.LogCritical(
                        "Conversation outbox event {EventId} exhausted retries and is being dead-lettered",
                        item.EventId);
                }

                try
                {
                    await outbox.MarkFailedAsync(
                        item.EventId,
                        _leaseOwner,
                        "realtime_publish_failed",
                        timeProvider.GetUtcNow().UtcDateTime.Add(retryDelay),
                        deadLetter,
                        cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception persistenceException)
                {
                    // Leave the lease intact. Another claim can safely retry after expiry.
                    logger.LogCritical(
                        persistenceException,
                        "Conversation outbox event {EventId} failure state could not be persisted",
                        item.EventId);
                }
            }
        }

        return items.Count > 0;
    }

    private static TimeSpan GetRetryDelay(int attemptCount)
    {
        var ceilingSeconds = Math.Min(
            Math.Pow(2, Math.Clamp(attemptCount, 1, 8)),
            300);
        var jitteredSeconds = ceilingSeconds * (0.5 + (Random.Shared.NextDouble() * 0.5));
        return TimeSpan.FromSeconds(jitteredSeconds);
    }
}
