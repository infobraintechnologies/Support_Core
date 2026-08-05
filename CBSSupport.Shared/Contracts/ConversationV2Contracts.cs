using System.ComponentModel.DataAnnotations;

namespace CBSSupport.Shared.Contracts;

public sealed record ConversationSummary(
    long Id,
    long ClientId,
    string Kind,
    string State,
    int? ClientUserId,
    string? ClientDisplayName,
    long? AdminUserId,
    string? AdminDisplayName,
    long LatestSequence,
    long LastReadSequence,
    long UnreadCount,
    DateTime CreatedAt,
    long Version);

public sealed record ConversationPage<T>(
    IReadOnlyList<T> Items,
    long? NextCursor);

public sealed record CreatePrivateConversationRequest(
    [Range(1, long.MaxValue)] long CounterpartyUserId);

public sealed record SendMessageV2Request(
    Guid ClientMessageId,
    [StringLength(4000)] string? Text,
    IReadOnlyList<Guid>? AttachmentIds = null) : IValidatableObject
{
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        var normalizedText = Text?.Trim();
        var attachmentIds = AttachmentIds ?? [];
        if (string.IsNullOrWhiteSpace(normalizedText) && attachmentIds.Count == 0)
        {
            yield return new ValidationResult(
                "Text or at least one attachment is required.",
                [nameof(Text), nameof(AttachmentIds)]);
        }

        if (attachmentIds.Count > 5)
        {
            yield return new ValidationResult(
                "A message may contain at most five attachments.",
                [nameof(AttachmentIds)]);
        }

        if (attachmentIds.Any(id => id == Guid.Empty)
            || attachmentIds.Distinct().Count() != attachmentIds.Count)
        {
            yield return new ValidationResult(
                "Attachment IDs must be non-empty and distinct.",
                [nameof(AttachmentIds)]);
        }
    }
}

public sealed record AdvanceConversationReadRequest(
    [Range(0, long.MaxValue)] long ThroughSequence);

public sealed record TransferConversationRequest(
    [Range(1, long.MaxValue)] long AdminUserId,
    [Range(1, long.MaxValue)] long ExpectedVersion,
    [StringLength(500)] string? Reason);

public sealed record ArchiveConversationRequest(
    [Range(1, long.MaxValue)] long ExpectedVersion);

public sealed record ReviewPrivateConversationRequest(
    [Range(1, int.MaxValue)] int ClientUserId,
    [Range(1, long.MaxValue)] long AdminUserId,
    [Range(1, long.MaxValue)] long ExpectedVersion,
    [Required, StringLength(500, MinimumLength = 1)] string Reason);

public sealed record ConversationDirectoryUser(
    long Id,
    string DisplayName);

public sealed record RealtimeEnvelope<T>(
    Guid EventId,
    int SchemaVersion,
    string EventType,
    DateTime OccurredAt,
    long ConversationId,
    long Sequence,
    T Data);

public enum ConversationCommandStatus
{
    Created,
    Replayed,
    Success,
    Unavailable,
    Conflict,
    Invalid
}

public sealed record ConversationCommandResult<T>(
    ConversationCommandStatus Status,
    T? Value = default,
    string? ErrorCode = null);
