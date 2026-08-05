using CBSSupport.Shared.Contracts;

namespace CBSSupport.Shared.Services;

public interface IConversationOutboxRepository
{
    Task<IReadOnlyList<ConversationOutboxItem>> ClaimAsync(
        string leaseOwner,
        int batchSize,
        DateTime now,
        DateTime leaseUntil,
        CancellationToken cancellationToken = default);

    Task MarkProcessedAsync(
        Guid eventId,
        string leaseOwner,
        DateTime processedAt,
        CancellationToken cancellationToken = default);

    Task<IAsyncDisposable?> AcquireDeliveryLeaseAsync(
        long conversationId,
        string expectedState,
        long expectedVersion,
        CancellationToken cancellationToken = default);

    Task MarkFailedAsync(
        Guid eventId,
        string leaseOwner,
        string errorCode,
        DateTime availableAt,
        bool deadLetter,
        CancellationToken cancellationToken = default);
}
