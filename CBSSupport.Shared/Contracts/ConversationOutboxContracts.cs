namespace CBSSupport.Shared.Contracts;

public sealed record ConversationOutboxItem(
    Guid EventId,
    long ConversationId,
    long? MessageId,
    string EventType,
    int SchemaVersion,
    DateTime OccurredAt,
    int AttemptCount,
    long ClientId,
    string ConversationKind,
    string ConversationState,
    int? ClientUserId,
    long? AdminUserId,
    long AccessVersion,
    string CurrentState,
    long CurrentVersion,
    ConversationMessage? Message);
