namespace CBSSupport.Shared.Contracts;

public sealed record ConversationMessage(
    long Id,
    long ConversationId,
    string Text,
    DateTime SentAt,
    ConversationSender Sender,
    string? AttachmentId);
