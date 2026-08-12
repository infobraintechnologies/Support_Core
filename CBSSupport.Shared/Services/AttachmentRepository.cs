using System.Text.Json;
using CBSSupport.Shared.Data;
using CBSSupport.Shared.Contracts;
using Dapper;
using Npgsql;

namespace CBSSupport.Shared.Services;

public sealed class AttachmentRepository(
    string connectionString,
    ISecurityAuditWriter? securityAudit = null) : IAttachmentRepository
{
    private readonly ISecurityAuditWriter _securityAudit = securityAudit ?? new NullSecurityAuditWriter();

    static AttachmentRepository()
    {
        SqlMapper.SetTypeMap(
            typeof(AttachmentRow),
            new CustomPropertyTypeMap(
                typeof(AttachmentRow),
                static (type, columnName) => type.GetProperties().FirstOrDefault(
                    property => string.Equals(
                        NormalizeColumnName(property.Name),
                        NormalizeColumnName(columnName),
                        StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException(
                        $"Attachment column '{columnName}' is not mapped.")));
    }

    private const string Columns = """
        SELECT id AS Id,
               client_id AS ClientId,
               conversation_id AS ConversationId,
               message_id AS MessageId,
               position AS Position,
               admin_user_id AS AdminUserId,
               client_user_id AS ClientUserId,
               state AS State,
               quarantine_key AS QuarantineKey,
               ready_key AS ReadyKey,
               display_name AS DisplayName,
               declared_media_type AS DeclaredMediaType,
               detected_media_type AS DetectedMediaType,
               declared_size AS DeclaredSize,
               actual_size AS ActualSize,
               reservation_bytes AS ReservationBytes,
               source_etag AS SourceETag,
               expected_ready_etag AS ExpectedReadyETag,
               sha256 AS Sha256,
               created_at AS CreatedAt,
               updated_at AS UpdatedAt,
               upload_completed_at AS UploadCompletedAt,
               ready_at AS ReadyAt,
               bound_at AS BoundAt,
               expires_at AS ExpiresAt,
               deleted_at AS DeletedAt,
               lease_owner AS LeaseOwner,
               lease_until AS LeaseUntil,
               attempt_count AS AttemptCount,
               next_attempt_at AS NextAttemptAt,
               rejection_code AS RejectionCode,
               delete_target_state AS DeleteTargetState,
               last_error_code AS LastErrorCode,
               deletion_attempt_count AS DeletionAttemptCount
        FROM digital.attachments
        """;

    public async Task<AttachmentCommandResult<AttachmentRecord>> CreateIntentAsync(
        AttachmentIntentRecord intent,
        AttachmentOptions options,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            "SELECT pg_advisory_xact_lock(hashtextextended('cbs-support:attachment-tenant:' || @ClientId, 0));",
            new { intent.ClientId },
            transaction,
            cancellationToken: cancellationToken));

        const string accessSql = """
            SELECT TRUE
            FROM digital.conversation_access access
            WHERE access.conversation_id = @ConversationId
              AND access.client_id = @TargetClientId
              AND access.state = 'Active'
              AND (@IsAdmin OR EXISTS (
                    SELECT 1
                    FROM internal.support_users authenticated_client
                    WHERE authenticated_client.id = @UserId
                      AND authenticated_client.client_id = access.client_id
                      AND authenticated_client.client_id = @TargetClientId
                      AND authenticated_client.status IS TRUE
                      AND authenticated_client.deactive_date IS NULL))
              AND (
                    (@IsAdmin AND (
                        access.conversation_kind IN ('Group','Ticket','Inquiry')
                        OR access.admin_user_id = @UserId))
                    OR (NOT @IsAdmin
                        AND (
                            access.conversation_kind IN ('Group','Ticket','Inquiry')
                            OR access.client_user_id = @UserId))
              );
            """;
        var hasAccess = await connection.QuerySingleOrDefaultAsync<bool?>(new CommandDefinition(
            accessSql,
            CreateIntentParameters(intent, new
            {
                intent.ConversationId
            }),
            transaction,
            cancellationToken: cancellationToken)) ?? false;
        if (!hasAccess)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            return new(AttachmentCommandStatus.Unavailable, ErrorCode: "conversation_unavailable");
        }

        const string quotaSql = """
            SELECT COALESCE((
                       SELECT active_storage_limit_bytes
                       FROM digital.attachment_tenant_quotas
                       WHERE client_id = @TargetClientId), @DefaultTenantQuota) AS TenantLimit,
                   COALESCE(sum(reservation_bytes) FILTER (
                       WHERE state IN (
                           'PendingUpload','Uploaded','StructuralValidation',
                           'StructurallyValidated','Scanning','Promoting','Ready','DeletePending')), 0)::bigint AS TenantUsed,
                   count(*) FILTER (
                       WHERE message_id IS NULL
                         AND state IN ('PendingUpload','Uploaded','StructuralValidation',
                                       'StructurallyValidated','Scanning','Promoting','Ready')
                         AND (
                              (@IsAdmin AND admin_user_id = @UserId)
                              OR (NOT @IsAdmin AND client_user_id = @UserId)
                         )) AS UserUnbound,
                   COALESCE(sum(declared_size) FILTER (
                       WHERE created_at >= @RollingStart
                         AND (
                              (@IsAdmin AND admin_user_id = @UserId)
                              OR (NOT @IsAdmin AND client_user_id = @UserId)
                         )), 0)::bigint AS UserRollingBytes
            FROM digital.attachments
            WHERE client_id = @TargetClientId;
            """;
        var quota = await connection.QuerySingleAsync<QuotaRow>(new CommandDefinition(
            quotaSql,
            CreateIntentParameters(intent, new
            {
                DefaultTenantQuota = options.DefaultTenantQuotaBytes,
                RollingStart = intent.CreatedAt.AddHours(-24)
            }),
            transaction,
            cancellationToken: cancellationToken));
        if (quota.UserUnbound >= options.MaximumConcurrentUnboundPerUser)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            return new(
                AttachmentCommandStatus.QuotaExceeded,
                ErrorCode: "attachment_unbound_limit");
        }
        if (quota.UserRollingBytes + intent.DeclaredSize > options.MaximumUserBytesPerRollingDay)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            return new(
                AttachmentCommandStatus.QuotaExceeded,
                ErrorCode: "attachment_user_daily_quota");
        }
        if (quota.TenantUsed + intent.DeclaredSize > quota.TenantLimit)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            return new(
                AttachmentCommandStatus.QuotaExceeded,
                ErrorCode: "attachment_tenant_storage_quota");
        }
        var tenantQuotaUsedAfterIntent = quota.TenantUsed + intent.DeclaredSize;
        var tenantQuotaWarning =
            tenantQuotaUsedAfterIntent >= quota.TenantLimit * 0.8m;

        const string insertSql = """
            INSERT INTO digital.attachments (
                id, client_id, conversation_id,
                admin_user_id, client_user_id, state,
                quarantine_key, display_name, declared_media_type,
                declared_size, reservation_bytes, created_at, updated_at,
                next_attempt_at)
            VALUES (
                @Id, @ClientId, @ConversationId,
                @AdminUserId, @ClientUserId, 'PendingUpload',
                @QuarantineKey, @DisplayName, @DeclaredMediaType,
                @DeclaredSize, @DeclaredSize, @CreatedAt, @CreatedAt,
                @CreatedAt);

            INSERT INTO digital.attachment_audit (
                attachment_id, client_id, action, actor_kind,
                admin_user_id, client_user_id, occurred_at, details)
            VALUES (
                @Id, @ClientId, 'UploadIntentCreated', @ActorKind,
                @AdminUserId, @ClientUserId, @CreatedAt,
                jsonb_build_object(
                    'conversationId', @ConversationId,
                    'declaredSize', @DeclaredSize,
                    'tenantQuotaWarning', @TenantQuotaWarning));
            """;
        await connection.ExecuteAsync(new CommandDefinition(
            insertSql,
            new
            {
                intent.Id,
                intent.ClientId,
                intent.ConversationId,
                AdminUserId = intent.Actor.IsAdmin ? checked((int)intent.Actor.UserId) : (int?)null,
                ClientUserId = intent.Actor.IsAdmin
                    ? (int?)null
                    : checked((int)intent.Actor.UserId),
                ActorKind = intent.Actor.IsAdmin ? "Admin" : "Client",
                intent.QuarantineKey,
                intent.DisplayName,
                intent.DeclaredMediaType,
                intent.DeclaredSize,
                intent.CreatedAt,
                TenantQuotaWarning = tenantQuotaWarning
            },
            transaction,
            cancellationToken: cancellationToken));
        await transaction.CommitAsync(cancellationToken);
        if (tenantQuotaWarning)
        {
            AttachmentMetrics.RecordTenantQuotaWarning(
                intent.ClientId,
                tenantQuotaUsedAfterIntent,
                quota.TenantLimit);
        }
        var created = await GetByIdAsync(connection, intent.Id, null, cancellationToken)
            ?? throw new InvalidOperationException("Attachment insert did not return a row.");
        return new(AttachmentCommandStatus.Accepted, created);
    }

    public Task<AttachmentRecord?> GetAuthorizedAsync(
        Guid attachmentId,
        AttachmentActor actor,
        CancellationToken cancellationToken = default) =>
        GetAuthorizedCoreAsync(attachmentId, actor, requireBoundReady: false, cancellationToken);

    public Task<AttachmentRecord?> GetReadyForContentAsync(
        Guid attachmentId,
        AttachmentActor actor,
        CancellationToken cancellationToken = default) =>
        GetAuthorizedCoreAsync(attachmentId, actor, requireBoundReady: true, cancellationToken);

    public async Task<AttachmentCommandResult<AttachmentRecord>> CompleteAsync(
        Guid attachmentId,
        AttachmentActor actor,
        long actualSize,
        string sourceETag,
        DateTimeOffset completedAt,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        const string sql = """
            UPDATE digital.attachments attachment
            SET state = CASE
                    WHEN @ActualSize = attachment.declared_size
                         AND @ActualSize BETWEEN 1 AND 10485760
                        THEN 'Uploaded'
                    ELSE 'DeletePending'
                END,
                actual_size = @ActualSize,
                source_etag = @SourceETag,
                upload_completed_at = @CompletedAt,
                updated_at = @CompletedAt,
                next_attempt_at = @CompletedAt,
                delete_target_state = CASE
                    WHEN @ActualSize = attachment.declared_size
                         AND @ActualSize BETWEEN 1 AND 10485760
                        THEN NULL
                    ELSE 'Rejected'
                END,
                rejection_code = CASE
                    WHEN @ActualSize = attachment.declared_size
                         AND @ActualSize BETWEEN 1 AND 10485760
                        THEN NULL
                    ELSE 'size_mismatch'
                END,
                last_error_code = NULL
            WHERE attachment.id = @AttachmentId
              AND attachment.state = 'PendingUpload'
              AND attachment.message_id IS NULL
              AND (@IsAdmin OR EXISTS (
                    SELECT 1
                    FROM internal.support_users authenticated_client
                    WHERE authenticated_client.id = @UserId
                      AND authenticated_client.client_id = attachment.client_id
                      AND authenticated_client.client_id = @ClientId
                      AND authenticated_client.status IS TRUE
                      AND authenticated_client.deactive_date IS NULL))
              AND (
                    (@IsAdmin AND attachment.admin_user_id = @UserId)
                    OR (NOT @IsAdmin
                        AND attachment.client_id = @ClientId
                        AND attachment.client_user_id = @UserId)
              )
            RETURNING *;
            """;
        var row = await QueryAttachmentAsync(
            connection,
            new CommandDefinition(
                sql,
                ActorParameters(actor, new
                {
                    AttachmentId = attachmentId,
                    ActualSize = actualSize,
                    SourceETag = NormalizeETag(sourceETag),
                    CompletedAt = completedAt
                }),
                transaction,
                cancellationToken: cancellationToken));
        if (row is not null)
        {
            await InsertAuditAsync(
                connection,
                transaction,
                row,
                actor.IsAdmin ? "Admin" : "Client",
                actor,
                "UploadCompleted",
                new { actualSize, sourceETag = NormalizeETag(sourceETag) },
                completedAt,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new(AttachmentCommandStatus.Accepted, row);
        }

        var existing = await GetAuthorizedCoreAsync(
            connection,
            transaction,
            attachmentId,
            actor,
            requireBoundReady: false,
            cancellationToken);
        await transaction.RollbackAsync(CancellationToken.None);
        return existing is null
            ? new(AttachmentCommandStatus.Unavailable, ErrorCode: "attachment_not_found")
            : existing.State is AttachmentStates.Uploaded
                or AttachmentStates.StructuralValidation
                or AttachmentStates.StructurallyValidated
                or AttachmentStates.Scanning
                or AttachmentStates.Promoting
                or AttachmentStates.Ready
                ? new(AttachmentCommandStatus.Success, existing)
                : new(AttachmentCommandStatus.Conflict, existing, "attachment_state_conflict");
    }

    public async Task<AttachmentCommandResult<AttachmentRecord>> CancelAsync(
        Guid attachmentId,
        AttachmentActor actor,
        string rejectionCode,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        const string sql = """
            UPDATE digital.attachments attachment
            SET state = 'DeletePending',
                delete_target_state = 'Deleted',
                rejection_code = @RejectionCode,
                updated_at = @Now,
                next_attempt_at = @Now,
                last_error_code = NULL
            WHERE attachment.id = @AttachmentId
              AND attachment.message_id IS NULL
              AND attachment.state IN ('PendingUpload','Uploaded','StructuralValidation',
                                        'StructurallyValidated','Scanning','Promoting','Ready')
              AND (@IsAdmin OR EXISTS (
                    SELECT 1
                    FROM internal.support_users authenticated_client
                    WHERE authenticated_client.id = @UserId
                      AND authenticated_client.client_id = attachment.client_id
                      AND authenticated_client.client_id = @ClientId
                      AND authenticated_client.status IS TRUE
                      AND authenticated_client.deactive_date IS NULL))
              AND (
                    (@IsAdmin AND attachment.admin_user_id = @UserId)
                    OR (NOT @IsAdmin
                        AND attachment.client_id = @ClientId
                        AND attachment.client_user_id = @UserId)
              )
            RETURNING *;
            """;
        var row = await QueryAttachmentAsync(
            connection,
            new CommandDefinition(
                sql,
                ActorParameters(actor, new
                {
                    AttachmentId = attachmentId,
                    RejectionCode = rejectionCode,
                    Now = now
                }),
                transaction,
                cancellationToken: cancellationToken));
        if (row is not null)
        {
            await InsertAuditAsync(
                connection,
                transaction,
                row,
                actor.IsAdmin ? "Admin" : "Client",
                actor,
                "CancellationRequested",
                new { rejectionCode },
                now,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new(AttachmentCommandStatus.Accepted, row);
        }

        var existing = await GetAuthorizedCoreAsync(
            connection,
            transaction,
            attachmentId,
            actor,
            requireBoundReady: false,
            cancellationToken);
        await transaction.RollbackAsync(CancellationToken.None);
        return existing is null
            ? new(AttachmentCommandStatus.Unavailable, ErrorCode: "attachment_not_found")
            : new(AttachmentCommandStatus.Conflict, ErrorCode: "attachment_cancel_conflict");
    }

    public async Task<IWorkerLeadershipLease?> TryAcquireWorkerLeadershipAsync(
        string workerName,
        CancellationToken cancellationToken = default)
    {
        var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        var acquired = await connection.QuerySingleAsync<bool>(new CommandDefinition(
            "SELECT pg_try_advisory_lock(hashtextextended(@WorkerName, 0));",
            new { WorkerName = $"cbs-support:{workerName}" },
            cancellationToken: cancellationToken));
        if (!acquired)
        {
            await connection.DisposeAsync();
            return null;
        }
        return new AdvisoryLease(connection, workerName);
    }

    public async Task<IReadOnlyList<AttachmentRecord>> ClaimScanBatchAsync(
        string leaseOwner,
        int batchSize,
        DateTimeOffset now,
        DateTimeOffset leaseUntil,
        CancellationToken cancellationToken = default) =>
        await ClaimProcessingBatchAsync(
            leaseOwner,
            batchSize,
            now,
            leaseUntil,
            AttachmentSecurityMode.MalwareScanning,
            cancellationToken);

    public async Task<IReadOnlyList<AttachmentRecord>> ClaimProcessingBatchAsync(
        string leaseOwner,
        int batchSize,
        DateTimeOffset now,
        DateTimeOffset leaseUntil,
        AttachmentSecurityMode securityMode,
        CancellationToken cancellationToken = default)
    {
        var processingState = securityMode == AttachmentSecurityMode.StructuralValidationOnly
            ? AttachmentStates.StructuralValidation
            : AttachmentStates.Scanning;
        const string sql = """
            WITH candidates AS (
                SELECT id
                FROM digital.attachments
                WHERE state IN ('Uploaded', @ProcessingState, 'StructurallyValidated', 'Promoting')
                  AND next_attempt_at <= @Now
                  AND (lease_until IS NULL OR lease_until < @Now)
                ORDER BY next_attempt_at, created_at, id
                FOR UPDATE SKIP LOCKED
                LIMIT @BatchSize
            )
            UPDATE digital.attachments attachment
            SET state = CASE
                    WHEN attachment.state = 'Uploaded' THEN @ProcessingState
                    ELSE attachment.state
                END,
                lease_owner = @LeaseOwner,
                lease_until = @LeaseUntil,
                updated_at = @Now
            FROM candidates
            WHERE attachment.id = candidates.id
            RETURNING attachment.*;
            """;
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var rows = await QueryAttachmentsAsync(connection, new CommandDefinition(
            sql,
            new
            {
                LeaseOwner = leaseOwner,
                ProcessingState = processingState,
                BatchSize = Math.Clamp(batchSize, 1, 4),
                Now = now,
                LeaseUntil = leaseUntil
            },
            transaction,
            cancellationToken: cancellationToken));
        foreach (var row in rows)
        {
            await InsertAuditAsync(
                connection,
                transaction,
                row,
                "System",
                null,
                "ProcessingClaimed",
                new { securityMode = securityMode.ToString(), state = row.State },
                now,
                cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return rows;
    }

    public async Task<AttachmentRecord?> MarkStructurallyValidatedAsync(
        Guid attachmentId,
        string leaseOwner,
        string detectedMediaType,
        long canonicalSize,
        string sourceETag,
        byte[] sha256,
        string readyKey,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            UPDATE digital.attachments
            SET state = 'StructurallyValidated',
                detected_media_type = @DetectedMediaType,
                actual_size = @CanonicalSize,
                source_etag = @SourceETag,
                expected_ready_etag = @SourceETag,
                sha256 = @Sha256,
                ready_key = @ReadyKey,
                updated_at = @Now,
                last_error_code = NULL
            WHERE id = @AttachmentId
              AND state = 'StructuralValidation'
              AND lease_owner = @LeaseOwner
            RETURNING *;
            """;
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var row = await QueryAttachmentAsync(connection, new CommandDefinition(
            sql,
            new
            {
                AttachmentId = attachmentId,
                LeaseOwner = leaseOwner,
                DetectedMediaType = detectedMediaType,
                CanonicalSize = canonicalSize,
                SourceETag = NormalizeETag(sourceETag),
                Sha256 = sha256,
                ReadyKey = readyKey,
                Now = now
            },
            transaction,
            cancellationToken: cancellationToken));
        if (row is null)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            return null;
        }
        await InsertAuditAsync(
            connection,
            transaction,
            row,
            "System",
            null,
            "StructurallyValidated",
            new
            {
                securityMode = nameof(AttachmentSecurityMode.StructuralValidationOnly),
                detectedMediaType,
                canonicalSize
            },
            now,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return row;
    }

    public async Task<AttachmentRecord?> MarkPromotingAsync(
        Guid attachmentId,
        string leaseOwner,
        string detectedMediaType,
        long actualSize,
        string sourceETag,
        byte[] sha256,
        string readyKey,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            UPDATE digital.attachments
            SET state = 'Promoting',
                detected_media_type = @DetectedMediaType,
                actual_size = @ActualSize,
                source_etag = @SourceETag,
                expected_ready_etag = @SourceETag,
                sha256 = @Sha256,
                ready_key = @ReadyKey,
                updated_at = @Now,
                last_error_code = NULL
            WHERE id = @AttachmentId
              AND state IN ('Scanning','StructurallyValidated')
              AND lease_owner = @LeaseOwner
            RETURNING *;
            """;
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var row = await QueryAttachmentAsync(connection, new CommandDefinition(
            sql,
            new
            {
                AttachmentId = attachmentId,
                LeaseOwner = leaseOwner,
                DetectedMediaType = detectedMediaType,
                ActualSize = actualSize,
                SourceETag = NormalizeETag(sourceETag),
                Sha256 = sha256,
                ReadyKey = readyKey,
                Now = now
            },
            transaction,
            cancellationToken: cancellationToken));
        if (row is null)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            return null;
        }
        await InsertAuditAsync(
            connection,
            transaction,
            row,
            "System",
            null,
            "PromotionPrepared",
            new
            {
                detectedMediaType,
                actualSize,
                sourceETag = NormalizeETag(sourceETag)
            },
            now,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return row;
    }

    public async Task<bool> MarkReadyAsync(
        Guid attachmentId,
        string leaseOwner,
        string readyETag,
        DateTimeOffset now,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            UPDATE digital.attachments
            SET state = 'Ready',
                expected_ready_etag = @ReadyETag,
                ready_at = COALESCE(ready_at, @Now),
                expires_at = @ExpiresAt,
                updated_at = @Now,
                rejection_code = NULL,
                last_error_code = NULL
            WHERE id = @AttachmentId
              AND state = 'Promoting'
              AND lease_owner = @LeaseOwner
            RETURNING *;
            """;
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var row = await QueryAttachmentAsync(connection, new CommandDefinition(
            sql,
            new
            {
                AttachmentId = attachmentId,
                LeaseOwner = leaseOwner,
                ReadyETag = NormalizeETag(readyETag),
                Now = now,
                ExpiresAt = expiresAt
            },
            transaction,
            cancellationToken: cancellationToken));
        if (row is null)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            return false;
        }
        await InsertAuditAsync(
            connection,
            transaction,
            row,
            "System",
            null,
            "Ready",
            new { readyETag = NormalizeETag(readyETag), expiresAt },
            now,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task MarkRejectedForDeleteAsync(
        Guid attachmentId,
        string leaseOwner,
        string rejectionCode,
        string targetState,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            UPDATE digital.attachments
            SET state = 'DeletePending',
                delete_target_state = @TargetState,
                rejection_code = @RejectionCode,
                updated_at = @Now,
                lease_owner = NULL,
                lease_until = NULL,
                next_attempt_at = @Now,
                last_error_code = NULL
            WHERE id = @AttachmentId
              AND lease_owner = @LeaseOwner
            RETURNING *;
            """;
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var row = await QueryAttachmentAsync(connection, new CommandDefinition(
            sql,
            new
            {
                AttachmentId = attachmentId,
                LeaseOwner = leaseOwner,
                RejectionCode = rejectionCode,
                TargetState = targetState,
                Now = now
            },
            transaction,
            cancellationToken: cancellationToken));
        if (row is null)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            return;
        }
        await InsertAuditAsync(
            connection,
            transaction,
            row,
            "System",
            null,
            "RejectedForDeletion",
            new { rejectionCode, targetState },
            now,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task ReleaseScanForRetryAsync(
        Guid attachmentId,
        string leaseOwner,
        DateTimeOffset nextAttemptAt,
        string errorCode,
        bool consumeAttempt,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            UPDATE digital.attachments
            SET attempt_count = attempt_count + CASE WHEN @ConsumeAttempt THEN 1 ELSE 0 END,
                next_attempt_at = @NextAttemptAt,
                last_error_code = @ErrorCode,
                lease_owner = NULL,
                lease_until = NULL,
                updated_at = now()
            WHERE id = @AttachmentId
              AND lease_owner = @LeaseOwner
            RETURNING *;
            """;
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var row = await QueryAttachmentAsync(connection, new CommandDefinition(
            sql,
            new
            {
                AttachmentId = attachmentId,
                LeaseOwner = leaseOwner,
                NextAttemptAt = nextAttemptAt,
                ErrorCode = errorCode,
                ConsumeAttempt = consumeAttempt
            },
            transaction,
            cancellationToken: cancellationToken));
        if (row is null)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            return;
        }
        await InsertAuditAsync(
            connection,
            transaction,
            row,
            "System",
            null,
            "ProcessingRetryScheduled",
            new { errorCode, consumeAttempt, nextAttemptAt },
            DateTimeOffset.UtcNow,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task ReleaseDeletionForRetryAsync(
        Guid attachmentId,
        string leaseOwner,
        DateTimeOffset nextAttemptAt,
        string errorCode,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            UPDATE digital.attachments
            SET deletion_attempt_count = deletion_attempt_count + 1,
                next_attempt_at = @NextAttemptAt,
                last_error_code = @ErrorCode,
                lease_owner = NULL,
                lease_until = NULL,
                updated_at = now()
            WHERE id = @AttachmentId
              AND state = 'DeletePending'
              AND lease_owner = @LeaseOwner
            RETURNING *;
            """;
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var row = await QueryAttachmentAsync(connection, new CommandDefinition(
            sql,
            new
            {
                AttachmentId = attachmentId,
                LeaseOwner = leaseOwner,
                NextAttemptAt = nextAttemptAt,
                ErrorCode = errorCode
            },
            transaction,
            cancellationToken: cancellationToken));
        if (row is null)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            return;
        }
        await InsertAuditAsync(
            connection,
            transaction,
            row,
            "System",
            null,
            "DeletionRetryScheduled",
            new { errorCode, nextAttemptAt },
            DateTimeOffset.UtcNow,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<AttachmentRecord>> ClaimCleanupBatchAsync(
        string leaseOwner,
        int batchSize,
        DateTimeOffset now,
        DateTimeOffset leaseUntil,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            WITH due AS (
                SELECT id,
                       CASE
                           WHEN state = 'PendingUpload' THEN 'Deleted'
                           WHEN state = 'Ready' THEN 'Expired'
                           ELSE delete_target_state
                       END AS target_state,
                       CASE
                           WHEN state = 'PendingUpload' THEN 'upload_abandoned'
                           ELSE rejection_code
                       END AS target_rejection
                FROM digital.attachments
                WHERE (
                        state = 'DeletePending'
                        OR (state = 'PendingUpload'
                            AND created_at <= @PendingCutoff)
                        OR (state = 'Ready'
                            AND expires_at IS NOT NULL
                            AND expires_at <= @Now)
                      )
                  AND next_attempt_at <= @Now
                  AND (lease_until IS NULL OR lease_until < @Now)
                ORDER BY COALESCE(expires_at, created_at), id
                FOR UPDATE SKIP LOCKED
                LIMIT @BatchSize
            )
            UPDATE digital.attachments attachment
            SET state = 'DeletePending',
                delete_target_state = due.target_state,
                rejection_code = due.target_rejection,
                lease_owner = @LeaseOwner,
                lease_until = @LeaseUntil,
                updated_at = @Now
            FROM due
            WHERE attachment.id = due.id
            RETURNING attachment.*;
            """;
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var rows = await QueryAttachmentsAsync(connection, new CommandDefinition(
            sql,
            new
            {
                LeaseOwner = leaseOwner,
                BatchSize = Math.Clamp(batchSize, 1, 100),
                Now = now,
                PendingCutoff = now.AddHours(-1),
                LeaseUntil = leaseUntil
            },
            transaction,
            cancellationToken: cancellationToken));
        foreach (var row in rows)
        {
            await InsertAuditAsync(
                connection,
                transaction,
                row,
                "System",
                null,
                "DeletionClaimed",
                new
                {
                    targetState = row.DeleteTargetState,
                    rejectionCode = row.RejectionCode
                },
                now,
                cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return rows;
    }

    public async Task<IReadOnlyList<AttachmentRecord>> ClaimReadyQuarantineCleanupBatchAsync(
        string leaseOwner,
        int batchSize,
        DateTimeOffset now,
        DateTimeOffset leaseUntil,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            WITH candidates AS (
                SELECT id
                FROM digital.attachments
                WHERE state = 'Ready'
                  AND quarantine_key IS NOT NULL
                  AND next_attempt_at <= @Now
                  AND (expires_at IS NULL OR expires_at > @Now)
                  AND (lease_until IS NULL OR lease_until < @Now)
                ORDER BY ready_at, id
                FOR UPDATE SKIP LOCKED
                LIMIT @BatchSize
            )
            UPDATE digital.attachments attachment
            SET lease_owner = @LeaseOwner,
                lease_until = @LeaseUntil,
                updated_at = @Now
            FROM candidates
            WHERE attachment.id = candidates.id
            RETURNING attachment.*;
            """;
        await using var connection = new NpgsqlConnection(connectionString);
        return await QueryAttachmentsAsync(connection, new CommandDefinition(
            sql,
            new
            {
                LeaseOwner = leaseOwner,
                BatchSize = Math.Clamp(batchSize, 1, 100),
                Now = now,
                LeaseUntil = leaseUntil
            },
            cancellationToken: cancellationToken));
    }

    public async Task<bool> CompleteReadyQuarantineCleanupAsync(
        Guid attachmentId,
        string leaseOwner,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            UPDATE digital.attachments
            SET quarantine_key = NULL,
                lease_owner = NULL,
                lease_until = NULL,
                last_error_code = NULL,
                updated_at = @Now
            WHERE id = @AttachmentId
              AND state = 'Ready'
              AND lease_owner = @LeaseOwner
            RETURNING *;
            """;
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var row = await QueryAttachmentAsync(connection, new CommandDefinition(
            sql,
            new
            {
                AttachmentId = attachmentId,
                LeaseOwner = leaseOwner,
                Now = now
            },
            transaction,
            cancellationToken: cancellationToken));
        if (row is null)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            return false;
        }
        await InsertAuditAsync(
            connection,
            transaction,
            row,
            "System",
            null,
            "QuarantineDeleted",
            new { },
            now,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task ReleaseReadyQuarantineCleanupForRetryAsync(
        Guid attachmentId,
        string leaseOwner,
        DateTimeOffset nextAttemptAt,
        string errorCode,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            UPDATE digital.attachments
            SET next_attempt_at = @NextAttemptAt,
                last_error_code = @ErrorCode,
                lease_owner = NULL,
                lease_until = NULL,
                updated_at = now()
            WHERE id = @AttachmentId
              AND state = 'Ready'
              AND quarantine_key IS NOT NULL
              AND lease_owner = @LeaseOwner
            RETURNING *;
            """;
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var row = await QueryAttachmentAsync(connection, new CommandDefinition(
            sql,
            new
            {
                AttachmentId = attachmentId,
                LeaseOwner = leaseOwner,
                NextAttemptAt = nextAttemptAt,
                ErrorCode = errorCode
            },
            transaction,
            cancellationToken: cancellationToken));
        if (row is null)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            return;
        }
        await InsertAuditAsync(
            connection,
            transaction,
            row,
            "System",
            null,
            "QuarantineDeletionRetryScheduled",
            new { errorCode, nextAttemptAt },
            DateTimeOffset.UtcNow,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task FinalizeDeletionAsync(
        Guid attachmentId,
        string leaseOwner,
        string targetState,
        string? rejectionCode,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            UPDATE digital.attachments
            SET state = @TargetState,
                delete_target_state = NULL,
                reservation_bytes = 0,
                rejection_code = @RejectionCode,
                deleted_at = @Now,
                updated_at = @Now,
                lease_owner = NULL,
                lease_until = NULL,
                last_error_code = NULL
            WHERE id = @AttachmentId
              AND state = 'DeletePending'
              AND lease_owner = @LeaseOwner
              AND delete_target_state = @TargetState
            RETURNING *;
            """;
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var row = await QueryAttachmentAsync(connection, new CommandDefinition(
            sql,
            new
            {
                AttachmentId = attachmentId,
                LeaseOwner = leaseOwner,
                TargetState = targetState,
                RejectionCode = rejectionCode,
                Now = now
            },
            transaction,
            cancellationToken: cancellationToken));
        if (row is null)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            return;
        }
        if (row.MessageId is not null)
        {
            const string deactivateCompanyFileSql = """
                UPDATE admin.files company_file
                SET status = FALSE,
                    edit_date = @Now
                WHERE company_file.id = @AttachmentId::text
                  AND company_file.table_name = 'digital.instructions'
                  AND company_file.table_id = @MessageId::text
                  AND company_file.file_name = @ReadyKey;
                """;
            var deactivated = await connection.ExecuteAsync(new CommandDefinition(
                deactivateCompanyFileSql,
                new
                {
                    AttachmentId = attachmentId,
                    MessageId = row.MessageId.Value,
                    row.ReadyKey,
                    Now = now
                },
                transaction,
                cancellationToken: cancellationToken));
            if (deactivated != 1)
            {
                await transaction.RollbackAsync(CancellationToken.None);
                throw new InvalidOperationException(
                    "Bound attachment company file metadata is missing or conflicting.");
            }
        }
        await InsertAuditAsync(
            connection,
            transaction,
            row,
            "System",
            null,
            "DeletionFinalized",
            new { targetState, rejectionCode },
            now,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private async Task<AttachmentRecord?> GetAuthorizedCoreAsync(
        Guid attachmentId,
        AttachmentActor actor,
        bool requireBoundReady,
        CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        return await GetAuthorizedCoreAsync(
            connection,
            transaction: null,
            attachmentId,
            actor,
            requireBoundReady,
            cancellationToken);
    }

    private static async Task<AttachmentRecord?> GetAuthorizedCoreAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid attachmentId,
        AttachmentActor actor,
        bool requireBoundReady,
        CancellationToken cancellationToken)
    {
        var condition = requireBoundReady
            ? """
              AND state = 'Ready'
              AND message_id IS NOT NULL
              AND EXISTS (
                    SELECT 1
                    FROM admin.files company_file
                    WHERE company_file.id = digital.attachments.id::text
                      AND company_file.table_name = 'digital.instructions'
                      AND company_file.table_id = digital.attachments.message_id::text
                      AND company_file.file_name = digital.attachments.ready_key
                      AND company_file.status IS TRUE)
              """
            : string.Empty;
        var sql = Columns + "\n" + $$"""
            WHERE id = @AttachmentId
              {{condition}}
              AND (@IsAdmin OR EXISTS (
                    SELECT 1
                    FROM internal.support_users authenticated_client
                    WHERE authenticated_client.id = @UserId
                      AND authenticated_client.client_id = digital.attachments.client_id
                      AND authenticated_client.client_id = @ClientId
                      AND authenticated_client.status IS TRUE
                      AND authenticated_client.deactive_date IS NULL))
              AND (
                    (
                        message_id IS NULL
                        AND (
                            (@IsAdmin AND admin_user_id = @UserId)
                            OR (NOT @IsAdmin
                                AND client_id = @ClientId
                                AND client_user_id = @UserId)
                        )
                    )
                    OR (
                        message_id IS NOT NULL
                        AND EXISTS (
                            SELECT 1
                            FROM digital.conversation_access access
                            WHERE access.conversation_id =
                                  digital.attachments.conversation_id
                              AND access.state = 'Active'
                              AND (
                                (@IsAdmin AND (
                                    access.conversation_kind IN ('Group','Ticket','Inquiry')
                                    OR access.admin_user_id = @UserId))
                                OR (NOT @IsAdmin
                                    AND access.client_id = @ClientId
                                    AND (
                                        access.conversation_kind IN ('Group','Ticket','Inquiry')
                                        OR access.client_user_id = @UserId))
                              )
                        )
                    )
              );
            """;
        return await QueryAttachmentAsync(
            connection,
            new CommandDefinition(
                sql,
                ActorParameters(actor, new { AttachmentId = attachmentId }),
                transaction,
                cancellationToken: cancellationToken));
    }

    private static async Task<AttachmentRecord?> GetByIdAsync(
        NpgsqlConnection connection,
        Guid attachmentId,
        NpgsqlTransaction? transaction,
        CancellationToken cancellationToken) =>
        await QueryAttachmentAsync(
            connection,
            new CommandDefinition(
                Columns + "\nWHERE id = @AttachmentId;",
                new { AttachmentId = attachmentId },
                transaction,
                cancellationToken: cancellationToken));

    private async Task InsertAuditAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        AttachmentRecord attachment,
        string actorKind,
        AttachmentActor? actor,
        string action,
        object details,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO digital.attachment_audit (
                attachment_id, client_id, action, actor_kind,
                admin_user_id, client_user_id, occurred_at, details)
            VALUES (
                @AttachmentId, @ClientId, @Action, @ActorKind,
                @AdminUserId, @ClientUserId, @OccurredAt, CAST(@Details AS jsonb));
            """;
        await connection.ExecuteAsync(new CommandDefinition(
            sql,
            new
            {
                AttachmentId = attachment.Id,
                attachment.ClientId,
                Action = action,
                ActorKind = actorKind,
                AdminUserId = actor?.IsAdmin == true ? checked((int)actor.UserId) : (int?)null,
                ClientUserId = actor is { IsAdmin: false }
                    ? checked((int)actor.UserId)
                    : (int?)null,
                OccurredAt = occurredAt,
                Details = JsonSerializer.Serialize(details)
            },
            transaction,
            cancellationToken: cancellationToken));
        await _securityAudit.AppendAsync(
            connection,
            transaction,
            new SecurityAuditEvent(
                attachment.ClientId,
                actor?.IsAdmin == true
                    ? SecurityAuditActorKinds.Admin
                    : actor is null
                        ? SecurityAuditActorKinds.System
                        : SecurityAuditActorKinds.Client,
                actor?.UserId,
                "Attachment",
                attachment.Id.ToString("D"),
                action,
                SecurityAuditOutcomes.Success,
                occurredAt,
                System.Diagnostics.Activity.Current?.Id,
                null,
                new Dictionary<string, string?> { ["feature"] = "attachments" }),
            cancellationToken);
    }

    internal static DynamicParameters CreateIntentParameters(
        AttachmentIntentRecord intent,
        object? additional = null)
    {
        var values = ActorParameters(intent.Actor, additional);
        values.Add("TargetClientId", intent.ClientId);
        return values;
    }

    private static DynamicParameters ActorParameters(
        AttachmentActor actor,
        object? additional = null)
    {
        var values = new DynamicParameters(additional);
        values.Add("IsAdmin", actor.IsAdmin);
        values.Add(
            "UserId",
            actor.IsAdmin
                ? actor.UserId
                : actor.UserId is > 0 and <= int.MaxValue
                    ? checked((int)actor.UserId)
                    : null);
        values.Add("ClientId", actor.ClientId);
        return values;
    }

    private static string NormalizeETag(string value) => value.Trim().Trim('"');

    private static async Task<AttachmentRecord?> QueryAttachmentAsync(
        NpgsqlConnection connection,
        CommandDefinition command)
    {
        var row = await connection.QuerySingleOrDefaultAsync<AttachmentRow>(command);
        return row?.ToAttachmentRecord();
    }

    private static async Task<IReadOnlyList<AttachmentRecord>> QueryAttachmentsAsync(
        NpgsqlConnection connection,
        CommandDefinition command) =>
        (await connection.QueryAsync<AttachmentRow>(command))
        .Select(static row => row.ToAttachmentRecord())
        .ToArray();

    private static string NormalizeColumnName(string name) =>
        name.Replace("_", string.Empty, StringComparison.Ordinal);

    private static DateTimeOffset ToUtcOffset(DateTime value) =>
        new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    private sealed class AttachmentRow
    {
        public Guid Id { get; set; }
        public long ClientId { get; set; }
        public long ConversationId { get; set; }
        public long? MessageId { get; set; }
        public short? Position { get; set; }
        public int? AdminUserId { get; set; }
        public int? ClientUserId { get; set; }
        public string State { get; set; } = string.Empty;
        public string? QuarantineKey { get; set; }
        public string? ReadyKey { get; set; }
        public string DisplayName { get; set; } = string.Empty;
        public string DeclaredMediaType { get; set; } = string.Empty;
        public string? DetectedMediaType { get; set; }
        public long DeclaredSize { get; set; }
        public long? ActualSize { get; set; }
        public long ReservationBytes { get; set; }
        public string? SourceETag { get; set; }
        public string? ExpectedReadyETag { get; set; }
        public byte[]? Sha256 { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
        public DateTime? UploadCompletedAt { get; set; }
        public DateTime? ReadyAt { get; set; }
        public DateTime? BoundAt { get; set; }
        public DateTime? ExpiresAt { get; set; }
        public DateTime? DeletedAt { get; set; }
        public string? LeaseOwner { get; set; }
        public DateTime? LeaseUntil { get; set; }
        public int AttemptCount { get; set; }
        public DateTime NextAttemptAt { get; set; }
        public string? RejectionCode { get; set; }
        public string? DeleteTargetState { get; set; }
        public string? LastErrorCode { get; set; }
        public int DeletionAttemptCount { get; set; }

        public AttachmentRecord ToAttachmentRecord() =>
            new(
                Id,
                ClientId,
                ConversationId,
                MessageId,
                Position,
                AdminUserId,
                ClientUserId,
                State,
                QuarantineKey,
                ReadyKey,
                DisplayName,
                DeclaredMediaType,
                DetectedMediaType,
                DeclaredSize,
                ActualSize,
                ReservationBytes,
                SourceETag,
                ExpectedReadyETag,
                Sha256,
                ToUtcOffset(CreatedAt),
                ToUtcOffset(UpdatedAt),
                UploadCompletedAt is { } uploadCompletedAt
                    ? ToUtcOffset(uploadCompletedAt)
                    : null,
                ReadyAt is { } readyAt ? ToUtcOffset(readyAt) : null,
                BoundAt is { } boundAt ? ToUtcOffset(boundAt) : null,
                ExpiresAt is { } expiresAt ? ToUtcOffset(expiresAt) : null,
                DeletedAt is { } deletedAt ? ToUtcOffset(deletedAt) : null,
                LeaseOwner,
                LeaseUntil is { } leaseUntil ? ToUtcOffset(leaseUntil) : null,
                AttemptCount,
                ToUtcOffset(NextAttemptAt),
                RejectionCode,
                DeleteTargetState,
                LastErrorCode,
                DeletionAttemptCount);
    }

    private sealed record QuotaRow(
        long TenantLimit,
        long TenantUsed,
        long UserUnbound,
        long UserRollingBytes);

    private sealed class AdvisoryLease(
        NpgsqlConnection connection,
        string workerName) : IWorkerLeadershipLease
    {
        public async Task<bool> IsHeldAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                return await connection.QuerySingleAsync<int>(new CommandDefinition(
                    "SELECT 1;",
                    cancellationToken: cancellationToken)) == 1;
            }
            catch (Exception exception) when (
                exception is NpgsqlException
                    or InvalidOperationException
                    or ObjectDisposedException)
            {
                return false;
            }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await connection.ExecuteAsync(
                    "SELECT pg_advisory_unlock(hashtextextended(@WorkerName, 0));",
                    new { WorkerName = $"cbs-support:{workerName}" });
            }
            finally
            {
                await connection.DisposeAsync();
            }
        }
    }
}
