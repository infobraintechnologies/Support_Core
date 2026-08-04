using CBSSupport.Shared.Contracts;
using Dapper;
using Npgsql;

namespace CBSSupport.Shared.Services;

public sealed class ConversationOutboxRepository(
    string connectionString,
    bool attachmentsEnabled)
    : IConversationOutboxRepository
{
    public async Task<IReadOnlyList<ConversationOutboxItem>> ClaimAsync(
        string leaseOwner,
        int batchSize,
        DateTime now,
        DateTime leaseUntil,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            WITH candidates AS (
                SELECT event_id
                FROM digital.conversation_outbox
                WHERE processed_at IS NULL
                  AND dead_lettered_at IS NULL
                  AND available_at <= @Now
                  AND (lease_until IS NULL OR lease_until < @Now)
                ORDER BY occurred_at, event_id
                FOR UPDATE SKIP LOCKED
                LIMIT @BatchSize
            ), claimed AS (
                UPDATE digital.conversation_outbox outbox
                SET lease_owner = @LeaseOwner,
                    lease_until = @LeaseUntil,
                    attempt_count = outbox.attempt_count + 1
                FROM candidates
                WHERE outbox.event_id = candidates.event_id
                RETURNING outbox.*
            )
            SELECT claimed.event_id AS EventId,
                   claimed.conversation_id AS ConversationId,
                   claimed.message_id AS MessageId,
                   claimed.event_type AS EventType,
                   claimed.schema_version AS SchemaVersion,
                   claimed.occurred_at AS OccurredAt,
                   claimed.attempt_count AS AttemptCount,
                   claimed.client_id AS ClientId,
                   claimed.conversation_kind AS ConversationKind,
                   claimed.conversation_state AS ConversationState,
                   claimed.client_user_id AS ClientUserId,
                   claimed.admin_user_id AS AdminUserId,
                   claimed.access_version AS AccessVersion,
                   access.state AS CurrentState,
                   access.version AS CurrentVersion,
                   message.id AS MessageRecordId,
                   message.instruction AS MessageText,
                   message.datetime AS MessageSentAt,
                   COALESCE(message.insert_user, message.client_auth_user_id) AS SenderUserId,
                   CASE WHEN message.id IS NULL THEN NULL
                        WHEN message.client_auth_user_id IS NULL THEN 'Admin'
                        ELSE 'Client' END AS SenderKind,
                   CASE WHEN message.id IS NULL THEN NULL
                        WHEN message.client_auth_user_id IS NULL
                            THEN COALESCE(admin_user.full_name, admin_user.user_name, 'Support')
                        ELSE COALESCE(client_user.full_name, client_user.user_name, 'Client')
                   END AS SenderDisplayName,
                   message.client_message_id AS ClientMessageId,
                   message.conversation_sequence AS Sequence
            FROM claimed
            JOIN digital.conversation_access access
              ON access.conversation_id = claimed.conversation_id
            LEFT JOIN digital.instructions message ON message.id = claimed.message_id
            LEFT JOIN admin.users admin_user ON admin_user.id = message.insert_user
            LEFT JOIN internal.support_users client_user
                   ON client_user.id = message.client_auth_user_id
                  AND client_user.client_id = message.client_id
                  AND client_user.client_id = claimed.client_id
            ORDER BY claimed.occurred_at, claimed.event_id;
            """;

        await using var connection = new NpgsqlConnection(connectionString);
        var rows = (await connection.QueryAsync<OutboxRow>(new CommandDefinition(
            sql,
            new
            {
                LeaseOwner = leaseOwner,
                BatchSize = Math.Clamp(batchSize, 1, 100),
                Now = now,
                LeaseUntil = leaseUntil
            },
            cancellationToken: cancellationToken))).AsList();
        var messageIds = rows
            .Where(row => row.MessageRecordId is not null)
            .Select(row => row.MessageRecordId!.Value)
            .Distinct()
            .ToArray();
        List<OutboxAttachmentRow> attachmentRows =
            messageIds.Length == 0 || !attachmentsEnabled
            ? []
            : (await connection.QueryAsync<OutboxAttachmentRow>(new CommandDefinition(
                """
                SELECT message_id AS MessageId,
                       id AS Id,
                       display_name AS DisplayName,
                       COALESCE(detected_media_type, declared_media_type) AS MediaType,
                       COALESCE(actual_size, declared_size) AS Size,
                       state AS Status,
                       rejection_code AS RejectionCode,
                       position AS Position
                FROM digital.attachments
                WHERE message_id = ANY(@MessageIds)
                ORDER BY message_id, position;
                """,
                new { MessageIds = messageIds },
                cancellationToken: cancellationToken))).AsList();
        var attachments = attachmentRows
            .GroupBy(row => row.MessageId)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<AttachmentSummary>)group
                    .Select(row => new AttachmentSummary(
                        row.Id,
                        row.DisplayName,
                        row.MediaType,
                        row.Size,
                        row.Status,
                        row.RejectionCode,
                        row.Position))
                    .ToArray());
        return rows.Select(row => ToItem(
            row,
            row.MessageRecordId is null
                ? []
                : attachments.GetValueOrDefault(row.MessageRecordId.Value) ?? [])).ToArray();
    }

    public async Task MarkProcessedAsync(
        Guid eventId,
        string leaseOwner,
        DateTime processedAt,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            UPDATE digital.conversation_outbox
            SET processed_at = @ProcessedAt,
                lease_owner = NULL,
                lease_until = NULL,
                last_error_code = NULL
            WHERE event_id = @EventId
              AND lease_owner = @LeaseOwner
              AND processed_at IS NULL;
            """;
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.ExecuteAsync(new CommandDefinition(
            sql,
            new { EventId = eventId, LeaseOwner = leaseOwner, ProcessedAt = processedAt },
            cancellationToken: cancellationToken));
    }

    public async Task<IAsyncDisposable?> AcquireDeliveryLeaseAsync(
        long conversationId,
        string expectedState,
        long expectedVersion,
        CancellationToken cancellationToken = default)
    {
        var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        try
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "SELECT pg_advisory_lock(hashtextextended('cbs-support:conversation:' || @ConversationId, 0));",
                new { ConversationId = conversationId },
                cancellationToken: cancellationToken));
            var isCurrent = await connection.QuerySingleOrDefaultAsync<bool?>(new CommandDefinition(
                """
                SELECT TRUE
                FROM digital.conversation_access
                WHERE conversation_id = @ConversationId
                  AND state = @ExpectedState
                  AND version = @ExpectedVersion;
                """,
                new { ConversationId = conversationId, ExpectedState = expectedState, ExpectedVersion = expectedVersion },
                cancellationToken: cancellationToken)) ?? false;
            if (!isCurrent)
            {
                await ReleaseDeliveryLockAsync(connection, conversationId);
                await connection.DisposeAsync();
                return null;
            }

            return new DeliveryLease(connection, conversationId);
        }
        catch
        {
            if (connection.FullState.HasFlag(System.Data.ConnectionState.Open))
            {
                await ReleaseDeliveryLockAsync(connection, conversationId);
            }
            await connection.DisposeAsync();
            throw;
        }
    }

    public async Task MarkFailedAsync(
        Guid eventId,
        string leaseOwner,
        string errorCode,
        DateTime availableAt,
        bool deadLetter,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            UPDATE digital.conversation_outbox
            SET available_at = @AvailableAt,
                lease_owner = NULL,
                lease_until = NULL,
                last_error_code = @ErrorCode,
                dead_lettered_at = CASE WHEN @DeadLetter THEN @Now ELSE dead_lettered_at END
            WHERE event_id = @EventId
              AND lease_owner = @LeaseOwner
              AND processed_at IS NULL;
            """;
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.ExecuteAsync(new CommandDefinition(
            sql,
            new
            {
                EventId = eventId,
                LeaseOwner = leaseOwner,
                ErrorCode = errorCode,
                AvailableAt = availableAt,
                DeadLetter = deadLetter,
                Now = DateTime.UtcNow
            },
            cancellationToken: cancellationToken));
    }

    private static ConversationOutboxItem ToItem(
        OutboxRow row,
        IReadOnlyList<AttachmentSummary> attachments)
    {
        ConversationMessage? message = null;
        if (row.MessageRecordId is not null
            && row.MessageSentAt is not null
            && row.SenderUserId is not null
            && row.SenderKind is not null
            && row.SenderDisplayName is not null
            && row.Sequence is not null
            && (row.MessageText is not null || attachments.Count > 0))
        {
            message = new ConversationMessage(
                row.MessageRecordId.Value,
                row.ConversationId,
                row.MessageText,
                row.MessageSentAt.Value,
                new ConversationSender(
                    row.SenderUserId.Value,
                    row.SenderDisplayName,
                    row.SenderKind),
                row.ClientMessageId,
                row.Sequence.Value,
                attachments);
        }

        return new ConversationOutboxItem(
            row.EventId,
            row.ConversationId,
            row.MessageId,
            row.EventType,
            row.SchemaVersion,
            row.OccurredAt,
            row.AttemptCount,
            row.ClientId,
            row.ConversationKind,
            row.ConversationState,
            row.ClientUserId,
            row.AdminUserId,
            row.AccessVersion,
            row.CurrentState,
            row.CurrentVersion,
            message);
    }

    private sealed record OutboxRow(
        Guid EventId,
        long ConversationId,
        long? MessageId,
        string EventType,
        short SchemaVersion,
        DateTime OccurredAt,
        int AttemptCount,
        long ClientId,
        string ConversationKind,
        string ConversationState,
        int? ClientUserId,
        int? AdminUserId,
        long AccessVersion,
        string CurrentState,
        long CurrentVersion,
        long? MessageRecordId,
        string? MessageText,
        DateTime? MessageSentAt,
        long? SenderUserId,
        string? SenderKind,
        string? SenderDisplayName,
        Guid? ClientMessageId,
        long? Sequence);

    private sealed record OutboxAttachmentRow(
        long MessageId,
        Guid Id,
        string DisplayName,
        string MediaType,
        long Size,
        string Status,
        string? RejectionCode,
        short Position);

    private static Task ReleaseDeliveryLockAsync(
        NpgsqlConnection connection,
        long conversationId) =>
        connection.ExecuteAsync(
            "SELECT pg_advisory_unlock(hashtextextended('cbs-support:conversation:' || @ConversationId, 0));",
            new { ConversationId = conversationId });

    private sealed class DeliveryLease(
        NpgsqlConnection connection,
        long conversationId) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try
            {
                await ReleaseDeliveryLockAsync(connection, conversationId);
            }
            finally
            {
                await connection.DisposeAsync();
            }
        }
    }
}
