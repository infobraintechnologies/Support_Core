using System.Text.Json.Serialization;

namespace CBSSupport.Shared.Contracts;

public sealed record ConversationMessage(
    long Id,
    long ConversationId,
    string? Text,
    DateTime SentAt,
    ConversationSender Sender,
    Guid? ClientMessageId = null,
    long Sequence = 0,
    IReadOnlyList<AttachmentSummary>? Attachments = null)
{
    [JsonIgnore]
    public IReadOnlyList<AttachmentSummary> SafeAttachments => Attachments ?? [];
}
