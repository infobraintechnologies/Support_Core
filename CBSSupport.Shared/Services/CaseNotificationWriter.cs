using Dapper;
using Npgsql;

namespace CBSSupport.Shared.Services;

internal static class CaseNotificationWriter
{
    public static Task InsertAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long caseId,
        long clientId,
        string notificationEventType,
        long caseVersion,
        Guid outboxEventId,
        string eventIdempotencyKey,
        bool actorIsAdmin,
        int actorUserId,
        DateTime occurredAt,
        CancellationToken cancellationToken)
    {
        const string sql = """
            WITH recipients AS (
                SELECT 'Admin'::varchar AS recipient_kind,
                       admin_user.id AS admin_user_id,
                       NULL::integer AS client_user_id
                FROM admin.users admin_user
                WHERE NOT @ActorIsAdmin
                  AND admin_user.status IS TRUE
                  AND admin_user.deactive_date IS NULL
                  AND admin_user.id <> @ActorUserId

                UNION ALL

                SELECT 'Client'::varchar AS recipient_kind,
                       NULL::integer AS admin_user_id,
                       client_user.id AS client_user_id
                FROM internal.support_users client_user
                WHERE @ActorIsAdmin
                  AND client_user.client_id = @ClientId
                  AND client_user.status IS TRUE
                  AND client_user.deactive_date IS NULL
                  AND client_user.id <> @ActorUserId
            )
            INSERT INTO digital.case_notifications (
                event_id, case_id, client_id, recipient_kind, admin_user_id,
                client_user_id, event_type, case_version, idempotency_key,
                payload_version, payload, created_at)
            SELECT @OutboxEventId,
                   @CaseId,
                   @ClientId,
                   recipients.recipient_kind,
                   recipients.admin_user_id,
                   recipients.client_user_id,
                   @NotificationEventType,
                   @CaseVersion,
                   @EventIdempotencyKey || CASE
                       WHEN recipients.recipient_kind = 'Admin'
                           THEN ':admin:' || recipients.admin_user_id::text
                       ELSE ':client:' || recipients.client_user_id::text
                   END,
                   1,
                   jsonb_build_object(
                       'eventId', @OutboxEventId,
                       'caseId', @CaseId,
                       'eventType', @NotificationEventType,
                       'caseVersion', @CaseVersion,
                       'payloadVersion', 1,
                       'title', CASE
                           WHEN @NotificationEventType LIKE 'Ticket%' THEN 'Ticket update'
                           WHEN @NotificationEventType LIKE 'Inquiry%' THEN 'Inquiry update'
                           WHEN @NotificationEventType = 'CaseReplyCreated' THEN 'New case reply'
                           ELSE 'Support update' END,
                       'message', CASE
                           WHEN @NotificationEventType LIKE 'Ticket%' THEN 'A ticket was updated.'
                           WHEN @NotificationEventType LIKE 'Inquiry%' THEN 'An inquiry was updated.'
                           WHEN @NotificationEventType = 'CaseReplyCreated' THEN 'A case has a new reply.'
                           ELSE 'A support case was updated.' END),
                   @OccurredAt
            FROM recipients
            ON CONFLICT (idempotency_key) DO NOTHING;
            """;

        return connection.ExecuteAsync(new CommandDefinition(
            sql,
            new
            {
                CaseId = caseId,
                ClientId = clientId,
                NotificationEventType = notificationEventType,
                CaseVersion = caseVersion,
                OutboxEventId = outboxEventId,
                EventIdempotencyKey = eventIdempotencyKey,
                ActorIsAdmin = actorIsAdmin,
                ActorUserId = actorUserId,
                OccurredAt = occurredAt
            },
            transaction,
            cancellationToken: cancellationToken));
    }
}
