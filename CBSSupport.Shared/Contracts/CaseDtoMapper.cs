using CBSSupport.Shared.Models;
using CBSSupport.Shared.ViewModels;

namespace CBSSupport.Shared.Contracts;

/// <summary>
/// Converts persistence/domain case records into public /api/v1 DTOs so no
/// database entity, Dapper row type, or internal domain object is returned to
/// callers.
/// </summary>
public static class CaseDtoMapper
{
    public const string TicketOpenStatus = "Open";
    public const string TicketResolvedStatus = "Resolved";
    public const string InquiryPendingStatus = "Pending";
    public const string InquiryCompletedStatus = "Completed";

    public static TicketResponse ToTicket(
        ChatMessage created,
        string subject,
        string? priority,
        string description) =>
        new(
            created.Id,
            created.InstructionId ?? created.Id,
            subject,
            CaseTypes.Label(created.InstTypeId),
            NormalizedPriority(priority),
            TicketOpenStatus,
            created.ClientId,
            created.SenderName ?? string.Empty,
            created.SenderName ?? string.Empty,
            null,
            description,
            null,
            created.DateTime,
            created.ExpiryDate,
            created.CompletedOn,
            1);

    public static InquiryResponse ToInquiry(
        ChatMessage created,
        string topic,
        string? priority,
        string description) =>
        new(
            created.Id,
            created.InstructionId ?? created.Id,
            topic,
            CaseTypes.Label(created.InstTypeId),
            NormalizedPriority(priority),
            InquiryPendingStatus,
            created.ClientId,
            created.SenderName ?? string.Empty,
            created.SenderName ?? string.Empty,
            description,
            created.DateTime,
            created.CompletedOn,
            1);

    public static TicketResponse ToTicket(TicketViewModel view) =>
        new(
            view.Id,
            view.Id,
            view.Subject,
            CaseTypes.Label(view.InstTypeId),
            NormalizedPriority(view.Priority),
            view.Status,
            view.ClientId is 0 ? null : view.ClientId,
            view.ClientName,
            view.CreatedBy,
            view.ResolvedBy,
            view.Description,
            view.Remarks,
            view.Date,
            view.ExpiryDate,
            view.ResolvedDate,
            view.Version);

    public static InquiryResponse ToInquiry(InquiryViewModel view) =>
        new(
            view.Id,
            view.Id,
            view.Topic,
            CaseTypes.Label(view.InstTypeId),
            NormalizedPriority(view.Priority),
            view.Outcome,
            view.ClientId is 0 ? null : view.ClientId,
            view.ClientName,
            view.InquiredBy,
            view.Description,
            view.Date,
            view.ResolvedDate,
            view.Version);

    private static string NormalizedPriority(string? priority) =>
        string.IsNullOrWhiteSpace(priority) ? CasePriorities.Normal : priority.Trim();
}
