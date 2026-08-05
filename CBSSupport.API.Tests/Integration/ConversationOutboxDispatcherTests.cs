using CBSSupport.API.Realtime;
using CBSSupport.Shared.Contracts;
using CBSSupport.Shared.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CBSSupport.API.Tests.Integration;

public sealed class ConversationOutboxDispatcherTests
{
    [Fact]
    public async Task DispatchBatch_PublishSucceeds_MarksEventProcessed()
    {
        var item = CreateItem(attemptCount: 1);
        var repository = new RecordingOutboxRepository(item);
        var publisher = new RecordingPublisher();
        var dispatcher = CreateDispatcher(repository, publisher);

        var foundWork = await dispatcher.DispatchBatchAsync(CancellationToken.None);

        Assert.True(foundWork);
        Assert.Equal([item.EventId], repository.Processed);
        Assert.Empty(repository.Failed);
        Assert.Equal([item.EventId], publisher.Published);
    }

    [Fact]
    public async Task DispatchBatch_LastPublishAttempt_DeadLettersWithStableErrorCode()
    {
        var item = CreateItem(attemptCount: 3);
        var repository = new RecordingOutboxRepository(item);
        var publisher = new RecordingPublisher { Failure = new InvalidOperationException() };
        var dispatcher = CreateDispatcher(repository, publisher, maxAttempts: 3);

        await dispatcher.DispatchBatchAsync(CancellationToken.None);

        var failure = Assert.Single(repository.Failed);
        Assert.Equal(item.EventId, failure.EventId);
        Assert.Equal("realtime_publish_failed", failure.ErrorCode);
        Assert.True(failure.DeadLetter);
        Assert.Empty(repository.Processed);
    }

    [Fact]
    public async Task DispatchBatch_HostCancellation_DoesNotMarkClaimFailed()
    {
        var item = CreateItem(attemptCount: 1);
        var repository = new RecordingOutboxRepository(item);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var publisher = new RecordingPublisher
        {
            Failure = new OperationCanceledException(cancellation.Token)
        };
        var dispatcher = CreateDispatcher(repository, publisher);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => dispatcher.DispatchBatchAsync(cancellation.Token));

        Assert.Empty(repository.Processed);
        Assert.Empty(repository.Failed);
    }

    [Fact]
    public async Task DispatchBatch_AccessChangedAfterCommit_SuppressesStaleAudience()
    {
        var item = CreateItem(attemptCount: 1) with { CurrentVersion = 2 };
        var repository = new RecordingOutboxRepository(item);
        var publisher = new RecordingPublisher();
        var dispatcher = CreateDispatcher(repository, publisher);

        await dispatcher.DispatchBatchAsync(CancellationToken.None);

        Assert.Equal([item.EventId], repository.Processed);
        Assert.Empty(repository.Failed);
        Assert.Empty(publisher.Published);
    }

    [Fact]
    public async Task DispatchBatch_AccessChangesBetweenClaimAndPublish_SuppressesAudience()
    {
        var item = CreateItem(attemptCount: 1);
        var repository = new RecordingOutboxRepository(item) { GrantDeliveryLease = false };
        var publisher = new RecordingPublisher();
        var dispatcher = CreateDispatcher(repository, publisher);

        await dispatcher.DispatchBatchAsync(CancellationToken.None);

        Assert.Equal([item.EventId], repository.Processed);
        Assert.Empty(repository.Failed);
        Assert.Empty(publisher.Published);
    }

    private static ConversationOutboxDispatcher CreateDispatcher(
        IConversationOutboxRepository repository,
        IConversationRealtimePublisher publisher,
        int maxAttempts = 8) =>
        new(
            repository,
            publisher,
            Options.Create(new ConversationOutboxDispatcherOptions
            {
                MaxAttempts = maxAttempts
            }),
            new FixedTimeProvider(new DateTimeOffset(2026, 7, 22, 12, 0, 0, TimeSpan.Zero)),
            NullLogger<ConversationOutboxDispatcher>.Instance);

    private static ConversationOutboxItem CreateItem(int attemptCount) =>
        new(
            Guid.NewGuid(),
            123,
            null,
            "ConversationArchived",
            1,
            DateTime.UtcNow,
            attemptCount,
            42,
            "Group",
            ConversationStates.Active,
            null,
            null,
            1,
            ConversationStates.Active,
            1,
            null);

    private sealed class RecordingPublisher : IConversationRealtimePublisher
    {
        public Exception? Failure { get; init; }

        public List<Guid> Published { get; } = [];

        public Task PublishAsync(
            ConversationOutboxItem item,
            CancellationToken cancellationToken = default)
        {
            Published.Add(item.EventId);
            return Failure is null ? Task.CompletedTask : Task.FromException(Failure);
        }
    }

    private sealed record FailedCall(Guid EventId, string ErrorCode, bool DeadLetter);

    private sealed class RecordingOutboxRepository(params ConversationOutboxItem[] items)
        : IConversationOutboxRepository
    {
        public List<Guid> Processed { get; } = [];

        public List<FailedCall> Failed { get; } = [];

        public bool GrantDeliveryLease { get; init; } = true;

        public Task<IReadOnlyList<ConversationOutboxItem>> ClaimAsync(
            string leaseOwner,
            int batchSize,
            DateTime now,
            DateTime leaseUntil,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ConversationOutboxItem>>(items);

        public Task<IAsyncDisposable?> AcquireDeliveryLeaseAsync(
            long conversationId,
            string expectedState,
            long expectedVersion,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IAsyncDisposable?>(
                GrantDeliveryLease ? new NoOpDeliveryLease() : null);

        public Task MarkProcessedAsync(
            Guid eventId,
            string leaseOwner,
            DateTime processedAt,
            CancellationToken cancellationToken = default)
        {
            Processed.Add(eventId);
            return Task.CompletedTask;
        }

        public Task MarkFailedAsync(
            Guid eventId,
            string leaseOwner,
            string errorCode,
            DateTime availableAt,
            bool deadLetter,
            CancellationToken cancellationToken = default)
        {
            Failed.Add(new FailedCall(eventId, errorCode, deadLetter));
            return Task.CompletedTask;
        }
    }

    private sealed class NoOpDeliveryLease : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
