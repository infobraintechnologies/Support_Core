namespace CBSSupport.Shared.Contracts;

public sealed record ConversationAccess(
    long ConversationId,
    long? ClientId,
    short InstructionTypeId,
    short InstructionCategoryId);
