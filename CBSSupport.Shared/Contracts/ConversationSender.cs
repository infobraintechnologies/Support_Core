namespace CBSSupport.Shared.Contracts;

public sealed record ConversationSender(
    long UserId,
    string DisplayName,
    string Kind);
