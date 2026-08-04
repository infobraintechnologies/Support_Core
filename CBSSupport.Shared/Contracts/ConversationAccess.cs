namespace CBSSupport.Shared.Contracts;

public sealed record ConversationAccess(
    long ConversationId,
    long? ClientId,
    short InstructionTypeId,
    short InstructionCategoryId,
    string State = ConversationStates.Active,
    int? ClientUserId = null,
    long? AdminUserId = null,
    long Version = 1)
{
    public bool IsGroup => InstructionTypeId == ConversationTypes.SupportGroup;

    public bool IsPrivate => InstructionTypeId == ConversationTypes.SupportPrivate;

    public bool IsTicket => ConversationTypes.IsTicket(InstructionTypeId)
        && InstructionCategoryId == InstructionCategories.Ticket;

    public bool IsInquiry => ConversationTypes.IsInquiry(InstructionTypeId)
        && InstructionCategoryId == InstructionCategories.Inquiry;

    public bool IsCase => IsTicket || IsInquiry;
}
