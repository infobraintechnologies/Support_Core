using System.ComponentModel.DataAnnotations;
using System.Text;
using System.Text.Json;

namespace CBSSupport.Shared.Contracts;

/// <summary>Allowlisted query parameters shared by the Ticket and Inquiry list endpoints.</summary>
public sealed class CaseListQuery
{
    public const int DefaultPageSize = 25;
    public const int MaximumPageSize = 100;

    [Range(1, MaximumPageSize)]
    public int? PageSize { get; init; }

    [StringLength(1024)]
    public string? Cursor { get; init; }

    [StringLength(20)]
    public string? Status { get; init; }

    [StringLength(50)]
    public string? Type { get; init; }

    [StringLength(50)]
    public string? Priority { get; init; }

    [StringLength(20)]
    public string? Sort { get; init; }

    [StringLength(4)]
    public string? Direction { get; init; }

    [Range(1, long.MaxValue)]
    public long? ClientId { get; init; }
}

public sealed record CasePage<T>(
    IReadOnlyList<T> Items,
    int PageSize,
    string? NextCursor);

public sealed record CaseListCriteria(
    int PageSize,
    string Sort,
    string Direction,
    bool? IsCompleted,
    short? TypeCode,
    string? Priority,
    long? ClientId,
    CaseListCursor? Cursor);

public sealed record CaseListCursor(
    string Sort,
    string Direction,
    long Id,
    DateTime? CreatedAt,
    int? StatusRank,
    short? TypeCode,
    string? Priority);

public delegate bool TryResolveCaseType(string? label, out short code);

/// <summary>
/// Validates the public list allowlist and creates/reads opaque keyset cursors.
/// The cursor includes the primary-key tie-breaker so equal sort values cannot
/// cause duplicate or randomly ordered records across pages.
/// </summary>
public static class CasePagination
{
    public const string CreatedAtSort = "createdAt";
    public const string StatusSort = "status";
    public const string TypeSort = "type";
    public const string PrioritySort = "priority";
    public const string Descending = "desc";
    public const string Ascending = "asc";

    public static bool TryCreateTicketCriteria(
        CaseListQuery query,
        bool allowClientFilter,
        out CaseListCriteria? criteria,
        out string? error) =>
        TryCreateCriteria(
            query,
            allowClientFilter,
            CaseDtoMapper.TicketOpenStatus,
            CaseDtoMapper.TicketResolvedStatus,
            CaseTypes.TryResolveTicket,
            out criteria,
            out error);

    public static bool TryCreateInquiryCriteria(
        CaseListQuery query,
        bool allowClientFilter,
        out CaseListCriteria? criteria,
        out string? error) =>
        TryCreateCriteria(
            query,
            allowClientFilter,
            CaseDtoMapper.InquiryPendingStatus,
            CaseDtoMapper.InquiryCompletedStatus,
            CaseTypes.TryResolveInquiry,
            out criteria,
            out error);

    public static string EncodeCursor(CaseListCursor cursor)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(cursor));
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    public static bool TryDecodeCursor(string value, out CaseListCursor? cursor)
    {
        cursor = null;
        if (string.IsNullOrWhiteSpace(value) || value.Length > 1024)
        {
            return false;
        }

        try
        {
            var encoded = value.Replace('-', '+').Replace('_', '/');
            encoded = encoded.PadRight(encoded.Length + ((4 - encoded.Length % 4) % 4), '=');
            cursor = JsonSerializer.Deserialize<CaseListCursor>(Encoding.UTF8.GetString(Convert.FromBase64String(encoded)));
            return cursor is { Id: > 0 }
                && IsAllowedSort(cursor.Sort)
                && IsAllowedDirection(cursor.Direction)
                && HasValueForSort(cursor);
        }
        catch (JsonException)
        {
            return false;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static bool TryCreateCriteria(
        CaseListQuery query,
        bool allowClientFilter,
        string openStatus,
        string completedStatus,
        TryResolveCaseType tryResolveType,
        out CaseListCriteria? criteria,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(query);
        criteria = null;
        error = null;

        var pageSize = query.PageSize ?? CaseListQuery.DefaultPageSize;
        if (pageSize is < 1 or > CaseListQuery.MaximumPageSize)
        {
            error = $"pageSize must be between 1 and {CaseListQuery.MaximumPageSize}.";
            return false;
        }

        var sort = string.IsNullOrWhiteSpace(query.Sort) ? CreatedAtSort : query.Sort.Trim();
        if (!IsAllowedSort(sort))
        {
            error = "sort must be one of createdAt, status, type, or priority.";
            return false;
        }

        var direction = string.IsNullOrWhiteSpace(query.Direction) ? Descending : query.Direction.Trim().ToLowerInvariant();
        if (!IsAllowedDirection(direction))
        {
            error = "direction must be asc or desc.";
            return false;
        }

        bool? isCompleted = null;
        if (!string.IsNullOrWhiteSpace(query.Status))
        {
            if (string.Equals(query.Status.Trim(), openStatus, StringComparison.OrdinalIgnoreCase))
            {
                isCompleted = false;
            }
            else if (string.Equals(query.Status.Trim(), completedStatus, StringComparison.OrdinalIgnoreCase))
            {
                isCompleted = true;
            }
            else
            {
                error = $"status must be {openStatus} or {completedStatus}.";
                return false;
            }
        }

        short? typeCode = null;
        if (!string.IsNullOrWhiteSpace(query.Type))
        {
            if (!tryResolveType(query.Type, out var resolvedType))
            {
                error = "type is invalid.";
                return false;
            }

            typeCode = resolvedType;
        }

        if (!CasePriorities.TryNormalize(query.Priority, out var priority))
        {
            error = "priority is invalid.";
            return false;
        }

        if (!allowClientFilter && query.ClientId is not null)
        {
            error = "clientId is available only to administrators.";
            return false;
        }

        CaseListCursor? cursor = null;
        if (!string.IsNullOrWhiteSpace(query.Cursor)
            && (!TryDecodeCursor(query.Cursor, out cursor)
                || !string.Equals(cursor!.Sort, sort, StringComparison.Ordinal)
                || !string.Equals(cursor.Direction, direction, StringComparison.Ordinal)))
        {
            error = "cursor is invalid for the requested sort.";
            return false;
        }

        criteria = new(pageSize, sort, direction, isCompleted, typeCode, priority, query.ClientId, cursor);
        return true;
    }

    private static bool IsAllowedSort(string value) => value is CreatedAtSort or StatusSort or TypeSort or PrioritySort;

    private static bool IsAllowedDirection(string value) => value is Ascending or Descending;

    private static bool HasValueForSort(CaseListCursor cursor) => cursor.Sort switch
    {
        CreatedAtSort => cursor.CreatedAt is not null,
        StatusSort => cursor.StatusRank is 0 or 1,
        TypeSort => cursor.TypeCode is not null,
        PrioritySort => !string.IsNullOrWhiteSpace(cursor.Priority),
        _ => false
    };
}
