namespace CBSSupport.Shared.Contracts;

public sealed record ConversationActor(
    long UserId,
    long? ClientId,
    bool IsAdmin,
    string DisplayName);
