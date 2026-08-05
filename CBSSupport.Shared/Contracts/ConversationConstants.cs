namespace CBSSupport.Shared.Contracts;

public static class ConversationTypes
{
    public const short SupportGroup = 100;
    public const short SupportPrivate = 101;
    public const short InternalTeam = 105;
    public const short TrainingTicket = 110;
    public const short MigrationTicket = 111;
    public const short SetupTicket = 112;
    public const short CorrectionTicket = 113;
    public const short BugFixTicket = 114;
    public const short NewFeatureTicket = 115;
    public const short FeatureEnhancementTicket = 116;
    public const short BackendWorkaroundTicket = 117;
    public const short AccountsInquiry = 121;
    public const short SalesInquiry = 122;

    public static bool IsTicket(short value) =>
        value is >= TrainingTicket and <= BackendWorkaroundTicket;

    public static bool IsInquiry(short value) =>
        value is AccountsInquiry or SalesInquiry;

    public static bool IsCase(short value) => IsTicket(value) || IsInquiry(value);
}

public static class InstructionCategories
{
    public const short Support = 100;
    public const short Ticket = 101;
    public const short Inquiry = 102;
}

public static class ConversationStates
{
    public const string Active = "Active";
    public const string Archived = "Archived";
    public const string NeedsReview = "NeedsReview";
}

public static class ConversationParticipantKinds
{
    public const string Admin = "Admin";
    public const string Client = "Client";
}

public static class ConversationKinds
{
    public const string Group = "Group";
    public const string Private = "Private";
    public const string Ticket = "Ticket";
    public const string Inquiry = "Inquiry";
}
