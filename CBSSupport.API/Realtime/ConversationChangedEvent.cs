namespace CBSSupport.API.Realtime;

public sealed record ConversationChangedEvent(
    string ChangeType,
    string ConversationKind,
    long ClientId,
    int? ClientUserId,
    long? AdminUserId);
