namespace CBSSupport.API.Realtime;

public sealed record TypingChangedEvent(
    long ConversationId,
    long UserId,
    string DisplayName,
    bool IsTyping);
