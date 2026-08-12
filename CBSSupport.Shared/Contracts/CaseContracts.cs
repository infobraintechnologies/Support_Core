using System.ComponentModel.DataAnnotations;

namespace CBSSupport.Shared.Contracts;

/// <summary>
/// User-facing type labels for case creation and the public /api/v1 contract.
/// These are the only tokens accepted from callers; ownership and persistence
/// values are always derived server-side.
/// </summary>
public static class CaseTypes
{
    public const string Training = "Training";
    public const string Migration = "Migration";
    public const string Setup = "Setup";
    public const string Correction = "Correction";
    public const string BugFix = "BugFix";
    public const string NewFeature = "NewFeature";
    public const string FeatureEnhancement = "FeatureEnhancement";
    public const string BackendWorkaround = "BackendWorkaround";
    public const string Accounts = "Accounts";
    public const string Sales = "Sales";

    /// <summary>Maps a ticket type label to its persisted instruction type code.</summary>
    public static bool TryResolveTicket(string? label, out short code) =>
        TryResolve<short>(
            label,
            new Dictionary<string, short>(StringComparer.Ordinal)
            {
                [Training] = ConversationTypes.TrainingTicket,
                [Migration] = ConversationTypes.MigrationTicket,
                [Setup] = ConversationTypes.SetupTicket,
                [Correction] = ConversationTypes.CorrectionTicket,
                [BugFix] = ConversationTypes.BugFixTicket,
                [NewFeature] = ConversationTypes.NewFeatureTicket,
                [FeatureEnhancement] = ConversationTypes.FeatureEnhancementTicket,
                [BackendWorkaround] = ConversationTypes.BackendWorkaroundTicket
            },
            out code);

    /// <summary>Maps an inquiry type label to its persisted instruction type code.</summary>
    public static bool TryResolveInquiry(string? label, out short code) =>
        TryResolve<short>(
            label,
            new Dictionary<string, short>(StringComparer.Ordinal)
            {
                [Accounts] = ConversationTypes.AccountsInquiry,
                [Sales] = ConversationTypes.SalesInquiry
            },
            out code);

    /// <summary>Returns the public type label for an instruction type code.</summary>
    public static string Label(short code) => code switch
    {
        ConversationTypes.TrainingTicket => Training,
        ConversationTypes.MigrationTicket => Migration,
        ConversationTypes.SetupTicket => Setup,
        ConversationTypes.CorrectionTicket => Correction,
        ConversationTypes.BugFixTicket => BugFix,
        ConversationTypes.NewFeatureTicket => NewFeature,
        ConversationTypes.FeatureEnhancementTicket => FeatureEnhancement,
        ConversationTypes.BackendWorkaroundTicket => BackendWorkaround,
        ConversationTypes.AccountsInquiry => Accounts,
        ConversationTypes.SalesInquiry => Sales,
        _ => "General"
    };

    private static bool TryResolve<T>(
        string? label,
        IReadOnlyDictionary<string, T> map,
        out T value)
    {
        if (!string.IsNullOrWhiteSpace(label) && map.TryGetValue(label.Trim(), out var resolved))
        {
            value = resolved;
            return true;
        }

        value = default!;
        return false;
    }
}

/// <summary>Accepted persistence-aware priority tokens (stored verbatim in remarks JSON).</summary>
public static class CasePriorities
{
    public const string Low = "Low";
    public const string Normal = "Normal";
    public const string High = "High";
    public const string Urgent = "Urgent";

    public static bool TryNormalize(string? value, out string? priority)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            priority = null;
            return true;
        }

        priority = value.Trim() switch
        {
            var candidate when string.Equals(candidate, Low, StringComparison.OrdinalIgnoreCase) => Low,
            var candidate when string.Equals(candidate, Normal, StringComparison.OrdinalIgnoreCase) => Normal,
            var candidate when string.Equals(candidate, High, StringComparison.OrdinalIgnoreCase) => High,
            var candidate when string.Equals(candidate, Urgent, StringComparison.OrdinalIgnoreCase) => Urgent,
            _ => null
        };
        return priority is not null;
    }
}

public sealed record CreateTicketRequest(
    [Required, StringLength(200, MinimumLength = 1)] string? Subject,
    [Required, StringLength(4000, MinimumLength = 1)] string? Description,
    [Required] string? Type,
    [StringLength(50)] string? Priority) : IValidatableObject
{
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext) =>
        CasePriorities.TryNormalize(Priority, out _)
            ? []
            : [new ValidationResult(
                "The ticket priority is invalid.",
                [nameof(Priority)])];
}

public sealed record CreateInquiryRequest(
    [Required, StringLength(200, MinimumLength = 1)] string? Topic,
    [Required, StringLength(4000, MinimumLength = 1)] string? Description,
    [Required] string? Type,
    [StringLength(50)] string? Priority) : IValidatableObject
{
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext) =>
        CasePriorities.TryNormalize(Priority, out _)
            ? []
            : [new ValidationResult(
                "The inquiry priority is invalid.",
                [nameof(Priority)])];
}

public sealed record UpdateCaseStatusRequest(
    [Required] string? Status,
    [Range(1, long.MaxValue)] long ExpectedVersion);

public enum CaseMutationStatus
{
    Updated,
    NotFound,
    Conflict,
    InvalidState
}

public sealed record CaseMutationResult(
    CaseMutationStatus Status,
    long? Version = null,
    long? ClientId = null);

/// <summary>
/// Public ticket representation. Deliberately does not expose persistence-only
/// or Dapper row fields (numeric type codes, client_auth_user_id, insert/edit
/// audit columns, SenderName, notification flags, etc.).
/// </summary>
public sealed record TicketResponse(
    long Id,
    long ConversationId,
    string? Subject,
    string? Type,
    string? Priority,
    string? Status,
    long? ClientId,
    string? ClientName,
    string? CreatedByName,
    string? ResolvedByName,
    string? Description,
    string? Remarks,
    DateTime CreatedAt,
    DateTime? DueAt,
    DateTime? ResolvedAt,
    long Version);

public sealed record InquiryResponse(
    long Id,
    long ConversationId,
    string? Topic,
    string? Type,
    string? Priority,
    string? Status,
    long? ClientId,
    string? ClientName,
    string? InquiredByName,
    string? Description,
    DateTime CreatedAt,
    DateTime? ResolvedAt,
    long Version);
