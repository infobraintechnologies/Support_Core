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
                    SELECT notification_id AS Id,
                           case_id AS CaseId,
                           event_type AS EventType,
                           created_at AS CreatedAt,
                           read_at AS ReadAt,
                           payload ->> 'title' AS Title,
                           payload ->> 'message' AS Message
                    FROM digital.case_notifications
                    WHERE {RecipientPredicate(recipient)}
                      AND (
                          CAST(@CursorCreatedAt AS timestamptz) IS NULL
                          OR (created_at, notification_id) <
                             (
                                 CAST(@CursorCreatedAt AS timestamptz),
                                 CAST(@CursorId AS bigint)
                             )
                      )
                    ORDER BY created_at DESC, notification_id DESC
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
                    UPDATE digital.case_notifications
                    SET read_at = COALESCE(read_at, CURRENT_TIMESTAMP)
                    WHERE notification_id = @NotificationId
                      AND {RecipientPredicate(recipient)}
                    RETURNING notification_id AS Id,
                              case_id AS CaseId,
                              event_type AS EventType,
                              created_at AS CreatedAt,
                              read_at AS ReadAt,
                              payload ->> 'title' AS Title,
                              payload ->> 'message' AS Message;
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
                    """
                    SELECT notification_id AS Id, case_id AS CaseId, event_type AS EventType,
                           created_at AS CreatedAt, read_at AS ReadAt,
                           recipient_kind AS RecipientKind, admin_user_id AS AdminUserId,
                           client_id AS ClientId, client_user_id AS ClientUserId,
                           payload ->> 'title' AS Title, payload ->> 'message' AS Message,
                           (SELECT count(*)
                            FROM digital.case_notifications recipient_notifications
                            WHERE recipient_notifications.recipient_kind = notification.recipient_kind
                              AND recipient_notifications.admin_user_id IS NOT DISTINCT FROM notification.admin_user_id
                              AND recipient_notifications.client_id = notification.client_id
                              AND recipient_notifications.client_user_id IS NOT DISTINCT FROM notification.client_user_id
                              AND recipient_notifications.read_at IS NULL) AS UnreadCount
                    FROM digital.case_notifications notification
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

    private static NotificationResponse ToResponse(NotificationRow row) =>
        new(
            row.Id,
            row.CaseId,
            row.EventType,
            new DateTimeOffset(DateTime.SpecifyKind(row.CreatedAt, DateTimeKind.Utc)),
            row.ReadAt is null
                ? null
                : new DateTimeOffset(DateTime.SpecifyKind(row.ReadAt.Value, DateTimeKind.Utc)),
            row.Title ?? TitleFor(row.EventType),
            row.Message ?? MessageFor(row.EventType, row.CaseId)
        );

    private static string RecipientPredicate(NotificationRecipient recipient) =>
        recipient.IsAdmin
            ? "recipient_kind = 'Admin' AND admin_user_id = @UserId"
            : "recipient_kind = 'Client' AND client_id = @ClientId AND client_user_id = @UserId";

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

    private static string TitleFor(string eventType) =>
        eventType switch
        {
            "TicketCreated" => "New support ticket",
            "InquiryCreated" => "New inquiry",
            "TicketResolved" or "TicketReopened" or "TicketUpdated" => "Ticket update",
            "InquiryCompleted" or "InquiryReopened" => "Inquiry update",
            "CaseReplyCreated" => "New case reply",
            _ => "Support update",
        };

    private static string MessageFor(string eventType, long caseId) =>
        $"{TitleFor(eventType)} for case #{caseId}.";

    private record NotificationRow(
        long Id,
        long CaseId,
        string EventType,
        DateTime CreatedAt,
        DateTime? ReadAt,
        string? Title,
        string? Message
    );

    private sealed record NotificationEventRow(
        long Id,
        long CaseId,
        string EventType,
        DateTime CreatedAt,
        DateTime? ReadAt,
        string RecipientKind,
        int? AdminUserId,
        long ClientId,
        int? ClientUserId,
        string? Title,
        string? Message,
        long UnreadCount
    ) : NotificationRow(Id, CaseId, EventType, CreatedAt, ReadAt, Title, Message);

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
