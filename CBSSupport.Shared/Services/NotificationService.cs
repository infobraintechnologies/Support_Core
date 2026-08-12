using System.Globalization;
using System.Text;
using System.Text.Json;
using CBSSupport.Shared.Contracts;
using Dapper;
using Npgsql;

namespace CBSSupport.Shared.Services;

public sealed record NotificationRecipient(bool IsAdmin, int UserId, long? ClientId)
{
    public static NotificationRecipient FromActor(ConversationActor actor) =>
        new(actor.IsAdmin, checked((int)actor.UserId), actor.IsAdmin ? null : actor.ClientId);
}

public interface INotificationService
{
    Task<NotificationPage> ListAsync(
        NotificationRecipient recipient,
        int pageSize,
        string? cursor,
        CancellationToken cancellationToken = default
    );
    Task<NotificationChangedEvent?> MarkReadAsync(
        NotificationRecipient recipient,
        long notificationId,
        CancellationToken cancellationToken = default
    );
    Task<NotificationBulkReadResult> MarkAllReadAsync(
        NotificationRecipient recipient,
        CancellationToken cancellationToken = default
    );
    Task<IReadOnlyList<NotificationDelivery>> GetChangesForEventAsync(
        Guid eventId,
        CancellationToken cancellationToken = default
    );
}

/// Reads and mutates only the current principal's rows. It deliberately has no
/// caller-supplied tenant or recipient arguments.
public sealed class NotificationService(string connectionString) : INotificationService
{
    private const int MaxPageSize = 100;

    // Case text is deliberately not persisted in notification payloads. It is projected only
    // after the recipient filter has been applied, from the tenant-matching canonical root.
    private const string CasePresentationColumns = """
        CASE
            WHEN case_root.inst_category_id = 101 THEN 'Ticket'
            WHEN case_root.inst_category_id = 102 THEN 'Inquiry'
            ELSE NULL
        END AS CaseType,
        CASE
            WHEN case_root.inst_category_id = 101 THEN
                NULLIF(BTRIM(public.try_get_json_value(case_root.remarks, 'subject')), '')
            WHEN case_root.inst_category_id = 102 THEN
                NULLIF(BTRIM(case_type.inst_type_name), '')
            ELSE NULL
        END AS CaseSummary
        """;

    public async Task<NotificationPage> ListAsync(
        NotificationRecipient recipient,
        int pageSize,
        string? cursor,
        CancellationToken cancellationToken = default
    )
    {
        var take = Math.Clamp(pageSize, 1, MaxPageSize);
        var boundary = NotificationCursor.TryParse(cursor);
        await using var connection = new NpgsqlConnection(connectionString);
        var rows = (
            await connection.QueryAsync<NotificationRow>(
                new CommandDefinition(
                    $"""
                    SELECT notification.notification_id AS Id,
                           notification.case_id AS CaseId,
                           notification.event_type AS EventType,
                           notification.created_at AS CreatedAt,
                           notification.read_at AS ReadAt,
                           {CasePresentationColumns}
                    FROM digital.case_notifications notification
                    LEFT JOIN digital.instructions case_root
                      ON case_root.id = notification.case_id
                     AND case_root.client_id = notification.client_id
                     AND case_root.instruction_id = case_root.id
                    LEFT JOIN digital.inst_types case_type
                      ON case_type.id = case_root.inst_type_id
                    WHERE {RecipientPredicate(recipient, "notification")}
                      AND (
                          CAST(@CursorCreatedAt AS timestamptz) IS NULL
                          OR (notification.created_at, notification.notification_id) <
                             (
                                 CAST(@CursorCreatedAt AS timestamptz),
                                 CAST(@CursorId AS bigint)
                             )
                      )
                    ORDER BY notification.created_at DESC, notification.notification_id DESC
                    LIMIT @TakePlusOne;
                    """,
                    Parameters(
                        recipient,
                        new
                        {
                            CursorCreatedAt = boundary?.CreatedAt,
                            CursorId = boundary?.Id,
                            TakePlusOne = take + 1,
                        }
                    ),
                    cancellationToken: cancellationToken
                )
            )
        ).AsList();

        var hasMore = rows.Count > take;
        var items = rows.Take(take).Select(ToResponse).ToArray();
        var unreadCount = await CountUnreadAsync(connection, recipient, cancellationToken);
        var next =
            hasMore && items.Length > 0
                ? NotificationCursor.Format(items[^1].CreatedAt, items[^1].Id)
                : null;
        return new NotificationPage(items, next, unreadCount);
    }

    public async Task<NotificationChangedEvent?> MarkReadAsync(
        NotificationRecipient recipient,
        long notificationId,
        CancellationToken cancellationToken = default
    )
    {
        if (notificationId <= 0)
            return null;

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        try
        {
            var row = await connection.QuerySingleOrDefaultAsync<NotificationRow>(
                new CommandDefinition(
                    $"""
                    WITH updated AS (
                        UPDATE digital.case_notifications notification
                        SET read_at = COALESCE(notification.read_at, CURRENT_TIMESTAMP)
                        WHERE notification.notification_id = @NotificationId
                          AND {RecipientPredicate(recipient, "notification")}
                        RETURNING notification.notification_id AS Id,
                                  notification.case_id AS CaseId,
                                  notification.client_id AS ClientId,
                                  notification.event_type AS EventType,
                                  notification.created_at AS CreatedAt,
                                  notification.read_at AS ReadAt
                    )
                    SELECT updated.Id,
                           updated.CaseId,
                           updated.EventType,
                           updated.CreatedAt,
                           updated.ReadAt,
                           {CasePresentationColumns}
                    FROM updated
                    LEFT JOIN digital.instructions case_root
                      ON case_root.id = updated.CaseId
                     AND case_root.client_id = updated.ClientId
                     AND case_root.instruction_id = case_root.id
                    LEFT JOIN digital.inst_types case_type
                      ON case_type.id = case_root.inst_type_id;
                    """,
                    Parameters(recipient, new { NotificationId = notificationId }),
                    transaction: transaction,
                    cancellationToken: cancellationToken
                )
            );

            if (row is null)
            {
                await transaction.RollbackAsync(CancellationToken.None);
                return null;
            }

            var unreadCount = await CountUnreadAsync(
                connection,
                recipient,
                cancellationToken,
                transaction
            );

            await transaction.CommitAsync(cancellationToken);

            return new NotificationChangedEvent(ToResponse(row), unreadCount);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task<NotificationBulkReadResult> MarkAllReadAsync(
        NotificationRecipient recipient,
        CancellationToken cancellationToken = default
    )
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        try
        {
            var count = await connection.ExecuteAsync(
                new CommandDefinition(
                    $"""
                    UPDATE digital.case_notifications
                    SET read_at = CURRENT_TIMESTAMP
                    WHERE {RecipientPredicate(recipient)}
                      AND read_at IS NULL;
                    """,
                    Parameters(recipient, new { }),
                    transaction: transaction,
                    cancellationToken: cancellationToken
                )
            );

            var unreadCount = await CountUnreadAsync(
                connection,
                recipient,
                cancellationToken,
                transaction
            );

            await transaction.CommitAsync(cancellationToken);

            return new NotificationBulkReadResult(count, unreadCount);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task<IReadOnlyList<NotificationDelivery>> GetChangesForEventAsync(
        Guid eventId,
        CancellationToken cancellationToken = default
    )
    {
        await using var connection = new NpgsqlConnection(connectionString);
        var rows = (
            await connection.QueryAsync<NotificationEventRow>(
                new CommandDefinition(
                    $"""
                    SELECT notification.notification_id AS Id,
                           notification.case_id AS CaseId,
                           notification.event_type AS EventType,
                           notification.created_at AS CreatedAt,
                           notification.read_at AS ReadAt,
                           {CasePresentationColumns},
                           recipient_kind AS RecipientKind, admin_user_id AS AdminUserId,
                           client_id AS ClientId, client_user_id AS ClientUserId,
                           (SELECT count(*)
                            FROM digital.case_notifications recipient_notifications
                            WHERE recipient_notifications.recipient_kind = notification.recipient_kind
                              AND recipient_notifications.admin_user_id IS NOT DISTINCT FROM notification.admin_user_id
                              AND recipient_notifications.client_id = notification.client_id
                              AND recipient_notifications.client_user_id IS NOT DISTINCT FROM notification.client_user_id
                              AND recipient_notifications.read_at IS NULL) AS UnreadCount
                    FROM digital.case_notifications notification
                    LEFT JOIN digital.instructions case_root
                      ON case_root.id = notification.case_id
                     AND case_root.client_id = notification.client_id
                     AND case_root.instruction_id = case_root.id
                    LEFT JOIN digital.inst_types case_type
                      ON case_type.id = case_root.inst_type_id
                    WHERE notification.event_id = @EventId
                    ORDER BY notification.notification_id;
                    """,
                    new { EventId = eventId },
                    cancellationToken: cancellationToken
                )
            )
        ).AsList();
        return rows.Select(row => new NotificationDelivery(
                string.Equals(row.RecipientKind, "Admin", StringComparison.Ordinal),
                row.ClientId,
                row.AdminUserId
                    ?? row.ClientUserId
                    ?? throw new InvalidOperationException("Notification recipient is missing."),
                new NotificationChangedEvent(ToResponse(row), row.UnreadCount)
            ))
            .ToArray();
    }

    private static NotificationResponse ToResponse(NotificationRow row)
    {
        var caseType = CaseTypeFor(row.CaseType, row.EventType);
        var caseReference = $"{caseType} #{row.CaseId}";
        return new(
            row.Id,
            row.CaseId,
            row.EventType,
            new DateTimeOffset(DateTime.SpecifyKind(row.CreatedAt, DateTimeKind.Utc)),
            row.ReadAt is null
                ? null
                : new DateTimeOffset(DateTime.SpecifyKind(row.ReadAt.Value, DateTimeKind.Utc)),
            TitleFor(row.EventType, caseReference),
            MessageFor(row.EventType, caseType, caseReference, row.CaseSummary)
        );
    }

    private static string RecipientPredicate(NotificationRecipient recipient, string? tableAlias = null)
    {
        var prefix = string.IsNullOrWhiteSpace(tableAlias) ? string.Empty : $"{tableAlias}.";
        return recipient.IsAdmin
            ? $"{prefix}recipient_kind = 'Admin' AND {prefix}admin_user_id = @UserId"
            : $"{prefix}recipient_kind = 'Client' AND {prefix}client_id = @ClientId AND {prefix}client_user_id = @UserId";
    }

    private static DynamicParameters Parameters(NotificationRecipient recipient, object values)
    {
        var parameters = new DynamicParameters(values);
        parameters.Add("UserId", recipient.UserId);
        parameters.Add("ClientId", recipient.ClientId);
        return parameters;
    }

    private static async Task<long> CountUnreadAsync(
        NpgsqlConnection connection,
        NotificationRecipient recipient,
        CancellationToken cancellationToken,
        NpgsqlTransaction? transaction = null
    ) =>
        await connection.QuerySingleAsync<long>(
            new CommandDefinition(
                $"SELECT count(*) FROM digital.case_notifications WHERE {RecipientPredicate(recipient)} AND read_at IS NULL;",
                Parameters(recipient, new { }),
                transaction,
                cancellationToken: cancellationToken
            )
        );

    private static string CaseTypeFor(string? caseType, string eventType) =>
        caseType is "Ticket" or "Inquiry"
            ? caseType
            : eventType.StartsWith("Ticket", StringComparison.Ordinal)
                ? "Ticket"
                : eventType.StartsWith("Inquiry", StringComparison.Ordinal)
                    ? "Inquiry"
                    : "Support case";

    private static string TitleFor(string eventType, string caseReference) =>
        eventType switch
        {
            "TicketCreated" => $"New ticket · {caseReference}",
            "InquiryCreated" => $"New inquiry · {caseReference}",
            "TicketResolved" => $"{caseReference} resolved",
            "TicketReopened" => $"{caseReference} reopened",
            "TicketUpdated" => $"{caseReference} updated",
            "InquiryCompleted" => $"{caseReference} completed",
            "InquiryReopened" => $"{caseReference} reopened",
            "CaseReplyCreated" => $"New reply on {caseReference}",
            _ => $"{caseReference} updated",
        };

    private static string MessageFor(
        string eventType,
        string caseType,
        string caseReference,
        string? caseSummary)
    {
        var summary = string.IsNullOrWhiteSpace(caseSummary) ? caseReference : caseSummary.Trim();
        var action = eventType switch
        {
            "TicketCreated" => "A new ticket was created.",
            "InquiryCreated" => "A new inquiry was created.",
            "TicketResolved" => "This ticket was resolved.",
            "TicketReopened" => "This ticket was reopened.",
            "TicketUpdated" => "Ticket details were updated.",
            "InquiryCompleted" => "This inquiry was completed.",
            "InquiryReopened" => "This inquiry was reopened.",
            "CaseReplyCreated" => "A new reply was added.",
            _ => $"This {caseType.ToLowerInvariant()} was updated.",
        };
        return $"{summary} — {action}";
    }

    private record NotificationRow(
        long Id,
        long CaseId,
        string EventType,
        DateTime CreatedAt,
        DateTime? ReadAt,
        string? CaseType,
        string? CaseSummary
    );

    private sealed record NotificationEventRow(
        long Id,
        long CaseId,
        string EventType,
        DateTime CreatedAt,
        DateTime? ReadAt,
        string? CaseType,
        string? CaseSummary,
        string RecipientKind,
        int? AdminUserId,
        long ClientId,
        int? ClientUserId,
        long UnreadCount
    ) : NotificationRow(Id, CaseId, EventType, CreatedAt, ReadAt, CaseType, CaseSummary);

    private sealed record NotificationCursor(DateTime CreatedAt, long Id)
    {
        public static NotificationCursor? TryParse(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return null;
            try
            {
                var raw = Encoding.UTF8.GetString(Convert.FromBase64String(value));
                var cursor = JsonSerializer.Deserialize<NotificationCursor>(raw);
                return cursor is { Id: > 0 } && cursor.CreatedAt.Kind == DateTimeKind.Utc
                    ? cursor
                    : null;
            }
            catch (FormatException)
            {
                return null;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        public static string Format(DateTimeOffset createdAt, long id) =>
            Convert.ToBase64String(
                Encoding.UTF8.GetBytes(
                    JsonSerializer.Serialize(new NotificationCursor(createdAt.UtcDateTime, id))
                )
            );
    }
}
