using System.Text.Json;
using CBSSupport.Shared.Contracts;
using CBSSupport.Shared.Models;
using Dapper;
using Npgsql;

namespace CBSSupport.Shared.Services;

public sealed class ConversationRepository : IConversationRepository
{
    private static readonly short[] AdminLegacyConversationTypeIds =
        [
            ConversationTypes.InternalTeam,
            ConversationTypes.TrainingTicket,
            ConversationTypes.MigrationTicket,
            ConversationTypes.SetupTicket,
            ConversationTypes.CorrectionTicket,
            ConversationTypes.BugFixTicket,
            ConversationTypes.NewFeatureTicket,
            ConversationTypes.FeatureEnhancementTicket,
            ConversationTypes.BackendWorkaroundTicket,
            ConversationTypes.AccountsInquiry,
            ConversationTypes.SalesInquiry
        ];

    private static readonly short[] ClientLegacyConversationTypeIds =
        [
            ConversationTypes.TrainingTicket,
            ConversationTypes.MigrationTicket,
            ConversationTypes.SetupTicket,
            ConversationTypes.CorrectionTicket,
            ConversationTypes.BugFixTicket,
            ConversationTypes.NewFeatureTicket,
            ConversationTypes.FeatureEnhancementTicket,
            ConversationTypes.BackendWorkaroundTicket,
            ConversationTypes.AccountsInquiry,
            ConversationTypes.SalesInquiry
        ];

    private const string AccessColumns = """
        SELECT ca.conversation_id AS ConversationId,
               ca.client_id AS ClientId,
               root.inst_type_id AS InstructionTypeId,
               root.inst_category_id AS InstructionCategoryId,
               ca.state AS State,
               ca.client_user_id AS ClientUserId,
               CAST(ca.admin_user_id AS bigint) AS AdminUserId,
               ca.version AS Version
        FROM digital.conversation_access ca
        JOIN digital.instructions root ON root.id = ca.conversation_id
        """;

    private const string SummaryColumns = """
        SELECT ca.conversation_id AS Id,
               ca.client_id AS ClientId,
               ca.conversation_kind AS Kind,
               ca.state AS State,
               ca.client_user_id AS ClientUserId,
               client_user.full_name AS ClientDisplayName,
               CAST(ca.admin_user_id AS bigint) AS AdminUserId,
               admin_user.full_name AS AdminDisplayName,
               COALESCE(seq.next_sequence - 1, 0) AS LatestSequence,
               COALESCE(cursor.last_read_sequence, 0) AS LastReadSequence,
               GREATEST(
                   COALESCE(seq.next_sequence - 1, 0) - COALESCE(cursor.last_read_sequence, 0),
                   0) AS UnreadCount,
               ca.created_at AS CreatedAt,
               ca.version AS Version
        FROM digital.conversation_access ca
        LEFT JOIN digital.conversation_sequences seq
               ON seq.conversation_id = ca.conversation_id
        LEFT JOIN internal.support_users client_user
               ON client_user.id = ca.client_user_id
              AND client_user.client_id = ca.client_id
        LEFT JOIN admin.users admin_user
               ON admin_user.id = ca.admin_user_id
        LEFT JOIN digital.conversation_read_cursors cursor
               ON cursor.conversation_id = ca.conversation_id
              AND ((@IsAdmin AND cursor.admin_user_id = @UserId)
                   OR (NOT @IsAdmin AND cursor.client_user_id = @UserId))
        """;

    private readonly string _connectionString;
    private readonly bool _attachmentsEnabled;

    public ConversationRepository(
        string connectionString,
        bool attachmentsEnabled)
    {
        _connectionString = connectionString;
        _attachmentsEnabled = attachmentsEnabled;
        DefaultTypeMap.MatchNamesWithUnderscores = true;
    }

    public async Task<ConversationCommandResult<ChatMessage>> CreateCaseAsync(
        ConversationActor actor,
        short instructionTypeId,
        short instructionCategoryId,
        string text,
        string? persistedRemarks,
        DateTime? expiryDate,
        string? ipAddress,
        DateTime occurredAt,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            var clientId = actor.ClientId!.Value;
            if (!await IsClientActorInTenantAsync(
                    connection,
                    transaction,
                    actor,
                    clientId,
                    cancellationToken))
            {
                await transaction.RollbackAsync(CancellationToken.None);
                return new(ConversationCommandStatus.Unavailable, ErrorCode: "client_identity_unavailable");
            }

            const string insertSql = """
                INSERT INTO digital.instructions (
                    datetime, inst_category_id, inst_type_id, instruction,
                    status, insert_user, client_auth_user_id, client_id,
                    service_id, ip_address, inst_channel, instruction_id,
                    remarks, expiry_date, client_message_id, conversation_sequence)
                VALUES (
                    @OccurredAt, @InstructionCategoryId, @InstructionTypeId, @Text,
                    TRUE, @AdminUserId, @ClientUserId, @ClientId,
                    3, @IpAddress, 'chat', NULL,
                    @PersistedRemarks, @ExpiryDate, NULL, 1)
                RETURNING id;
                """;
            var conversationId = await connection.QuerySingleAsync<long>(new CommandDefinition(
                insertSql,
                new
                {
                    OccurredAt = occurredAt,
                    InstructionCategoryId = instructionCategoryId,
                    InstructionTypeId = instructionTypeId,
                    Text = text,
                    AdminUserId = actor.IsAdmin ? checked((int)actor.UserId) : (int?)null,
                    ClientUserId = actor.IsAdmin ? (int?)null : checked((int)actor.UserId),
                    ClientId = clientId,
                    IpAddress = ipAddress,
                    PersistedRemarks = persistedRemarks,
                    ExpiryDate = expiryDate
                },
                transaction,
                cancellationToken: cancellationToken));

            var conversationKind = instructionCategoryId == InstructionCategories.Ticket
                ? ConversationKinds.Ticket
                : ConversationKinds.Inquiry;
            var eventId = Guid.NewGuid();
            const string initializeSql = """
                UPDATE digital.instructions
                SET instruction_id = id
                WHERE id = @ConversationId
                  AND client_id = @ClientId;

                INSERT INTO digital.conversation_access (
                    conversation_id, client_id, conversation_kind, state,
                    client_user_id, admin_user_id, version, created_at)
                VALUES (
                    @ConversationId, @ClientId, @ConversationKind, 'Active',
                    NULL, NULL, 1, @OccurredAt);

                INSERT INTO digital.conversation_sequences (
                    conversation_id, next_sequence)
                VALUES (@ConversationId, 2);

                INSERT INTO digital.conversation_audit (
                    conversation_id, client_id, action, actor_kind,
                    admin_user_id, client_user_id, occurred_at, details)
                VALUES (
                    @ConversationId, @ClientId, 'CaseCreated', @ActorKind,
                    @AdminUserId, @ClientUserId, @OccurredAt,
                    jsonb_build_object('conversationKind', @ConversationKind));

                INSERT INTO digital.conversation_outbox (
                    event_id, conversation_id, client_id, conversation_kind,
                    conversation_state, client_user_id, admin_user_id, access_version,
                    message_id, event_type, schema_version, payload,
                    occurred_at, available_at, attempt_count)
                VALUES (
                    @EventId, @ConversationId, @ClientId, @ConversationKind,
                    'Active', NULL, NULL, 1,
                    @ConversationId, 'MessageCreated', 1,
                    jsonb_build_object(
                        'eventId', @EventId,
                        'conversationId', @ConversationId,
                        'messageId', @ConversationId,
                        'sequence', 1),
                    @OccurredAt, @OccurredAt, 0);
                """;
            await connection.ExecuteAsync(new CommandDefinition(
                initializeSql,
                new
                {
                    ConversationId = conversationId,
                    ClientId = clientId,
                    ConversationKind = conversationKind,
                    OccurredAt = occurredAt,
                    EventId = eventId,
                    ActorKind = actor.IsAdmin ? "Admin" : "Client",
                    AdminUserId = actor.IsAdmin ? checked((int)actor.UserId) : (int?)null,
                    ClientUserId = actor.IsAdmin ? (int?)null : checked((int)actor.UserId)
                },
                transaction,
                cancellationToken: cancellationToken));

            const string selectSql = """
                SELECT i.id,
                       i.datetime,
                       i.inst_category_id,
                       i.inst_type_id,
                       i.instruction,
                       i.status,
                       i.insert_date,
                       i.insert_user,
                       i.client_id,
                       i.client_auth_user_id,
                       i.instruction_id,
                       i.completed,
                       i.attachment_id,
                       i.client_message_id,
                       i.conversation_sequence,
                       i.inst_channel,
                       CASE
                           WHEN i.client_auth_user_id IS NOT NULL
                               THEN COALESCE(client_user.full_name, client_user.user_name, 'Unknown Client User')
                           ELSE COALESCE(admin_user.full_name, admin_user.user_name, 'Unknown Admin User')
                       END AS SenderName
                FROM digital.instructions i
                LEFT JOIN admin.users admin_user
                       ON admin_user.id = i.insert_user
                      AND i.client_auth_user_id IS NULL
                LEFT JOIN internal.support_users client_user
                       ON client_user.id = i.client_auth_user_id
                      AND client_user.client_id = i.client_id
                WHERE i.id = @ConversationId
                  AND i.client_id = @ClientId;
                """;
            var created = await connection.QuerySingleAsync<ChatMessage>(new CommandDefinition(
                selectSql,
                new { ConversationId = conversationId, ClientId = clientId },
                transaction,
                cancellationToken: cancellationToken));
            await transaction.CommitAsync(cancellationToken);
            return new(ConversationCommandStatus.Created, created);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task<ConversationAccess?> GetForAdminAsync(
        long conversationId,
        long adminUserId,
        CancellationToken cancellationToken = default)
    {
        const string sql = AccessColumns + "\n" + """
            WHERE ca.conversation_id = @ConversationId
              AND ca.state = 'Active'
              AND (
                    ca.conversation_kind IN ('Group', 'Ticket', 'Inquiry')
                    OR ca.admin_user_id = @AdminUserId
              );
            """;

        await using var connection = new NpgsqlConnection(_connectionString);
        var access = await connection.QuerySingleOrDefaultAsync<ConversationAccess>(new CommandDefinition(
            sql,
            new { ConversationId = conversationId, AdminUserId = checked((int)adminUserId) },
            cancellationToken: cancellationToken));
        return access ?? await GetLegacyAccessAsync(
            connection,
            conversationId,
            clientId: null,
            AdminLegacyConversationTypeIds,
            cancellationToken);
    }

    public Task<ConversationAccess?> GetForAdminAsync(
        long conversationId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<ConversationAccess?>(null);

    public async Task<ConversationAccess?> GetForClientAsync(
        long conversationId,
        long clientId,
        CancellationToken cancellationToken = default)
    {
        // Legacy callers cannot prove per-user private membership.
        const string sql = AccessColumns + "\n" + """
            WHERE ca.conversation_id = @ConversationId
              AND ca.client_id = @ClientId
              AND ca.state = 'Active'
              AND ca.conversation_kind IN ('Group', 'Ticket', 'Inquiry');
            """;
        await using var connection = new NpgsqlConnection(_connectionString);
        return await connection.QuerySingleOrDefaultAsync<ConversationAccess>(new CommandDefinition(
            sql,
            new { ConversationId = conversationId, ClientId = clientId },
            cancellationToken: cancellationToken));
    }

    public async Task<ConversationAccess?> GetForClientAsync(
        long conversationId,
        long clientId,
        int clientUserId,
        CancellationToken cancellationToken = default)
    {
        const string sql = AccessColumns + "\n" + """
            WHERE ca.conversation_id = @ConversationId
              AND ca.client_id = @ClientId
              AND ca.state = 'Active'
              AND (
                    ca.conversation_kind IN ('Group', 'Ticket', 'Inquiry')
                    OR ca.client_user_id = @ClientUserId
              );
            """;
        await using var connection = new NpgsqlConnection(_connectionString);
        if (!await IsSupportUserInTenantAsync(
                connection,
                transaction: null,
                clientUserId,
                clientId,
                cancellationToken))
        {
            return null;
        }

        var access = await connection.QuerySingleOrDefaultAsync<ConversationAccess>(new CommandDefinition(
            sql,
            new
            {
                ConversationId = conversationId,
                ClientId = clientId,
                ClientUserId = clientUserId
            },
            cancellationToken: cancellationToken));
        return access ?? await GetLegacyAccessAsync(
            connection,
            conversationId,
            clientId,
            ClientLegacyConversationTypeIds,
            cancellationToken);
    }

    private static Task<ConversationAccess?> GetLegacyAccessAsync(
        NpgsqlConnection connection,
        long conversationId,
        long? clientId,
        short[] allowedInstructionTypeIds,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT root.id AS ConversationId,
                   root.client_id AS ClientId,
                   root.inst_type_id AS InstructionTypeId,
                   root.inst_category_id AS InstructionCategoryId,
                   'Active' AS State,
                   NULL::integer AS ClientUserId,
                   NULL::bigint AS AdminUserId,
                   1::bigint AS Version
            FROM digital.instructions root
            WHERE root.id = @ConversationId
              AND root.instruction_id = root.id
              AND root.inst_type_id = ANY(@AllowedInstructionTypeIds)
              AND (
                    (root.inst_type_id = 105 AND root.inst_category_id = 100)
                 OR (root.inst_type_id BETWEEN 110 AND 117 AND root.inst_category_id = 101)
                 OR (root.inst_type_id IN (121, 122) AND root.inst_category_id = 102)
              )
              AND (@ClientId IS NULL OR root.client_id = @ClientId);
            """;

        return connection.QuerySingleOrDefaultAsync<ConversationAccess>(new CommandDefinition(
            sql,
            new
            {
                ConversationId = conversationId,
                ClientId = clientId,
                AllowedInstructionTypeIds = allowedInstructionTypeIds
            },
            cancellationToken: cancellationToken));
    }

    public Task<long?> InsertMessageForAdminAsync(
        long conversationId,
        int userId,
        string text,
        DateTime sentAt,
        string? ipAddress,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<long?>(null);

    public Task<long?> InsertMessageForClientAsync(
        long conversationId,
        long clientId,
        int userId,
        string text,
        DateTime sentAt,
        string? ipAddress,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<long?>(null);

    public async Task<IReadOnlyList<ConversationSummary>> ListAsync(
        ConversationActor actor,
        int limit,
        long? beforeConversationId,
        CancellationToken cancellationToken = default)
    {
        const string sql = SummaryColumns + "\n" + """
            WHERE ca.state = 'Active'
              AND (@BeforeConversationId IS NULL OR ca.conversation_id < @BeforeConversationId)
              AND (@IsAdmin OR EXISTS (
                    SELECT 1
                    FROM internal.support_users authenticated_client
                    WHERE authenticated_client.id = @UserId
                      AND authenticated_client.client_id = @ClientId
                      AND authenticated_client.status IS TRUE
                      AND authenticated_client.deactive_date IS NULL))
              AND (
                    (@IsAdmin AND (
                        ca.conversation_kind IN ('Group', 'Ticket', 'Inquiry')
                        OR ca.admin_user_id = @UserId))
                 OR (NOT @IsAdmin
                     AND ca.client_id = @ClientId
                     AND (
                        ca.conversation_kind IN ('Group', 'Ticket', 'Inquiry')
                        OR ca.client_user_id = @UserId))
              )
            ORDER BY ca.conversation_id DESC
            LIMIT @Limit;
            """;
        await using var connection = new NpgsqlConnection(_connectionString);
        var rows = await connection.QueryAsync<ConversationSummary>(new CommandDefinition(
            sql,
            ActorParameters(actor, new { Limit = limit, BeforeConversationId = beforeConversationId }),
            cancellationToken: cancellationToken));
        return rows.AsList();
    }

    public async Task<ConversationCommandResult<ConversationSummary>> GetOrCreateGroupAsync(
        ConversationActor actor,
        long clientId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var clientExists = await connection.QuerySingleOrDefaultAsync<bool?>(new CommandDefinition(
            "SELECT TRUE FROM internal.clients WHERE id = @ClientId;",
            new { ClientId = clientId },
            cancellationToken: cancellationToken)) ?? false;
        if (!clientExists)
        {
            return new(ConversationCommandStatus.Unavailable, ErrorCode: "client_unavailable");
        }
        if (!await IsClientActorInTenantAsync(
                connection,
                transaction: null,
                actor,
                clientId,
                cancellationToken))
        {
            return new(ConversationCommandStatus.Unavailable, ErrorCode: "client_identity_unavailable");
        }

        var existing = await FindSummaryAsync(
            connection,
            transaction: null,
            actor,
            "ca.conversation_kind = 'Group' AND ca.client_id = @TargetClientId",
            new { TargetClientId = clientId },
            cancellationToken);
        if (existing is not null)
        {
            return new(ConversationCommandStatus.Replayed, existing);
        }

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            const string createSql = """
                INSERT INTO digital.instructions (
                    datetime, inst_category_id, inst_type_id, instruction,
                    status, insert_user, client_auth_user_id, client_id,
                    service_id, inst_channel, instruction_id,
                    conversation_sequence)
                VALUES (
                    @Now, 100, 100, NULL,
                    TRUE, NULL, NULL, @ClientId,
                    3, 'chat', NULL, 0)
                RETURNING id;
                """;
            var now = DateTime.UtcNow;
            var conversationId = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
                createSql,
                new { ClientId = clientId, Now = now },
                transaction,
                cancellationToken: cancellationToken));

            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE digital.instructions SET instruction_id = id WHERE id = @ConversationId;",
                new { ConversationId = conversationId },
                transaction,
                cancellationToken: cancellationToken));
            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO digital.conversation_access (
                    conversation_id, client_id, conversation_kind, state,
                    client_user_id, admin_user_id, version, created_at)
                VALUES (@ConversationId, @ClientId, 'Group', 'Active', NULL, NULL, 1, @Now);

                INSERT INTO digital.conversation_sequences (conversation_id, next_sequence)
                VALUES (@ConversationId, 1);
                """,
                new { ConversationId = conversationId, ClientId = clientId, Now = now },
                transaction,
                cancellationToken: cancellationToken));
            await InsertAuditAsync(
                connection,
                transaction,
                conversationId,
                clientId,
                "Created",
                actor,
                details: null,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            var created = await FindSummaryAsync(
                connection,
                null,
                actor,
                "ca.conversation_id = @ConversationId",
                new { ConversationId = conversationId },
                cancellationToken);
            return new(ConversationCommandStatus.Created, created);
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            var winner = await FindSummaryAsync(
                connection,
                null,
                actor,
                "ca.conversation_kind = 'Group' AND ca.client_id = @TargetClientId",
                new { TargetClientId = clientId },
                cancellationToken);
            return winner is null
                ? new(ConversationCommandStatus.Conflict, ErrorCode: "conversation_conflict")
                : new(ConversationCommandStatus.Replayed, winner);
        }
    }

    public async Task<ConversationCommandResult<ConversationSummary>> GetOrCreatePrivateAsync(
        ConversationActor actor,
        long counterpartyUserId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        long clientId;
        int clientUserId;
        long adminUserId;
        if (actor.IsAdmin)
        {
            if (counterpartyUserId is <= 0 or > int.MaxValue)
            {
                return new(ConversationCommandStatus.Unavailable, ErrorCode: "counterparty_unavailable");
            }

            var targetClientUserId = checked((int)counterpartyUserId);
            const string clientSql = """
                SELECT client_id
                FROM internal.support_users
                WHERE id = @UserId AND status IS TRUE AND deactive_date IS NULL;
                """;
            var targetClientId = await connection.QuerySingleOrDefaultAsync<long?>(new CommandDefinition(
                clientSql,
                new { UserId = targetClientUserId },
                cancellationToken: cancellationToken));
            if (targetClientId is null)
            {
                return new(ConversationCommandStatus.Unavailable, ErrorCode: "counterparty_unavailable");
            }

            clientId = targetClientId.Value;
            clientUserId = targetClientUserId;
            adminUserId = actor.UserId;
        }
        else
        {
            const string adminSql = """
                SELECT id
                FROM admin.users
                WHERE id = @UserId AND status IS TRUE AND deactive_date IS NULL;
                """;
            var targetAdminId = await connection.QuerySingleOrDefaultAsync<long?>(new CommandDefinition(
                adminSql,
                new { UserId = counterpartyUserId },
                cancellationToken: cancellationToken));
            if (targetAdminId is null || actor.ClientId is not > 0)
            {
                return new(ConversationCommandStatus.Unavailable, ErrorCode: "counterparty_unavailable");
            }

            clientId = actor.ClientId.Value;
            if (!await IsClientActorInTenantAsync(
                    connection,
                    transaction: null,
                    actor,
                    clientId,
                    cancellationToken))
            {
                return new(ConversationCommandStatus.Unavailable, ErrorCode: "client_identity_unavailable");
            }
            clientUserId = checked((int)actor.UserId);
            adminUserId = targetAdminId.Value;
        }

        var pairParameters = new { ClientUserId = clientUserId, AdminUserId = adminUserId };
        var existing = await FindSummaryAsync(
            connection,
            null,
            actor,
            """
            ca.conversation_kind = 'Private'
            AND ca.state = 'Active'
            AND ca.client_user_id = @ClientUserId
            AND ca.admin_user_id = @AdminUserId
            """,
            pairParameters,
            cancellationToken);
        if (existing is not null)
        {
            return new(ConversationCommandStatus.Replayed, existing);
        }

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            var now = DateTime.UtcNow;
            const string createSql = """
                INSERT INTO digital.instructions (
                    datetime, inst_category_id, inst_type_id, instruction,
                    status, insert_user, client_auth_user_id, client_id,
                    service_id, inst_channel, instruction_id,
                    conversation_sequence)
                VALUES (
                    @Now, 100, 101, NULL,
                    TRUE, NULL, NULL, @ClientId,
                    3, 'chat', NULL, 0)
                RETURNING id;
                """;
            var conversationId = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
                createSql,
                new { ClientId = clientId, Now = now },
                transaction,
                cancellationToken: cancellationToken));
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE digital.instructions SET instruction_id = id WHERE id = @ConversationId;",
                new { ConversationId = conversationId },
                transaction,
                cancellationToken: cancellationToken));
            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO digital.conversation_access (
                    conversation_id, client_id, conversation_kind, state,
                    client_user_id, admin_user_id, version, created_at)
                VALUES (
                    @ConversationId, @ClientId, 'Private', 'Active',
                    @ClientUserId, @AdminUserId, 1, @Now);

                INSERT INTO digital.conversation_sequences (conversation_id, next_sequence)
                VALUES (@ConversationId, 1);
                """,
                new
                {
                    ConversationId = conversationId,
                    ClientId = clientId,
                    ClientUserId = clientUserId,
                    AdminUserId = checked((int)adminUserId),
                    Now = now
                },
                transaction,
                cancellationToken: cancellationToken));
            await InsertAuditAsync(
                connection,
                transaction,
                conversationId,
                clientId,
                "Created",
                actor,
                new { clientUserId, adminUserId },
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            var created = await FindSummaryAsync(
                connection,
                null,
                actor,
                "ca.conversation_id = @ConversationId",
                new { ConversationId = conversationId },
                cancellationToken);
            return new(ConversationCommandStatus.Created, created);
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            var winner = await FindSummaryAsync(
                connection,
                null,
                actor,
                """
                ca.conversation_kind = 'Private'
                AND ca.state = 'Active'
                AND ca.client_user_id = @ClientUserId
                AND ca.admin_user_id = @AdminUserId
                """,
                pairParameters,
                cancellationToken);
            return winner is null
                ? new(ConversationCommandStatus.Conflict, ErrorCode: "conversation_conflict")
                : new(ConversationCommandStatus.Replayed, winner);
        }
    }

    public async Task<ConversationPage<ConversationMessage>?> GetMessagesAsync(
        long conversationId,
        ConversationActor actor,
        int limit,
        long? beforeSequence,
        long? afterSequence,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var access = await GetAccessForUpdateAsync(
            connection,
            transaction,
            conversationId,
            actor,
            cancellationToken);
        if (access is null)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            return null;
        }

        var comparison = beforeSequence.HasValue
            ? "AND i.conversation_sequence < @Cursor"
            : afterSequence.HasValue
                ? "AND i.conversation_sequence > @Cursor"
                : string.Empty;
        var direction = afterSequence.HasValue ? "ASC" : "DESC";
        var sql = $$"""
            SELECT i.id AS Id,
                   i.instruction_id AS ConversationId,
                   i.instruction AS Text,
                   i.datetime AS SentAt,
                   COALESCE(i.insert_user, i.client_auth_user_id) AS SenderUserId,
                   CASE WHEN i.client_auth_user_id IS NULL THEN 'Admin' ELSE 'Client' END AS SenderKind,
                   CASE WHEN i.client_auth_user_id IS NULL
                        THEN COALESCE(admin_user.full_name, admin_user.user_name, 'Support')
                        ELSE COALESCE(client_user.full_name, client_user.user_name, 'Client')
                   END AS SenderDisplayName,
                   i.client_message_id AS ClientMessageId,
                   i.conversation_sequence AS Sequence
            FROM digital.instructions i
            LEFT JOIN admin.users admin_user ON admin_user.id = i.insert_user
            LEFT JOIN internal.support_users client_user
                   ON client_user.id = i.client_auth_user_id
                  AND client_user.client_id = i.client_id
            WHERE i.instruction_id = @ConversationId
              AND i.conversation_sequence > 0
              {{comparison}}
            ORDER BY i.conversation_sequence {{direction}}
            LIMIT @Limit;
            """;
        var rows = (await connection.QueryAsync<MessageRow>(new CommandDefinition(
            sql,
            new
            {
                ConversationId = conversationId,
                Cursor = beforeSequence ?? afterSequence,
                Limit = limit + 1
            },
            transaction,
            cancellationToken: cancellationToken))).AsList();
        await transaction.CommitAsync(cancellationToken);
        var hasMore = rows.Count > limit;
        if (hasMore)
        {
            rows.RemoveAt(rows.Count - 1);
        }

        if (!afterSequence.HasValue)
        {
            rows.Reverse();
        }

        var attachments = _attachmentsEnabled
            ? await LoadAttachmentSummariesAsync(
                connection,
                transaction: null,
                rows.Select(row => row.Id).ToArray(),
                cancellationToken)
            : [];
        var messages = rows
            .Select(row => ToMessage(
                row,
                attachments.GetValueOrDefault(row.Id) ?? []))
            .ToArray();
        long? nextCursor = hasMore && messages.Length > 0
            ? beforeSequence.HasValue || !afterSequence.HasValue
                ? messages[0].Sequence
                : messages[^1].Sequence
            : null;
        return new ConversationPage<ConversationMessage>(messages, nextCursor);
    }

    public async Task<ConversationCommandResult<ConversationMessage>> SendMessageAsync(
        long conversationId,
        ConversationActor actor,
        Guid clientMessageId,
        string? text,
        IReadOnlyList<Guid> attachmentIds,
        string? ipAddress,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var access = await GetAccessForUpdateAsync(
            connection,
            transaction,
            conversationId,
            actor,
            cancellationToken);
        if (access is null)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            return new(ConversationCommandStatus.Unavailable, ErrorCode: "conversation_unavailable");
        }
        if (!_attachmentsEnabled && attachmentIds.Count > 0)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            return new(
                ConversationCommandStatus.Conflict,
                ErrorCode: "attachments_disabled");
        }

        // Serialize retries for the same caller-generated UUID across conversations.
        // This closes the check/insert race while the global unique index remains the
        // final database invariant.
        await connection.ExecuteAsync(new CommandDefinition(
            "SELECT pg_advisory_xact_lock(hashtextextended(@IdempotencyKey, 0));",
            new { IdempotencyKey = clientMessageId.ToString("D") },
            transaction,
            cancellationToken: cancellationToken));

        const string existingSql = """
            SELECT i.id AS Id,
                   i.instruction_id AS ConversationId,
                   i.instruction AS Text,
                   i.datetime AS SentAt,
                   COALESCE(i.insert_user, i.client_auth_user_id) AS SenderUserId,
                   CASE WHEN i.client_auth_user_id IS NULL THEN 'Admin' ELSE 'Client' END AS SenderKind,
                   @DisplayName AS SenderDisplayName,
                   i.client_message_id AS ClientMessageId,
                   i.conversation_sequence AS Sequence
            FROM digital.instructions i
            WHERE i.client_message_id = @ClientMessageId;
            """;
        var existing = await connection.QuerySingleOrDefaultAsync<MessageRow>(new CommandDefinition(
            existingSql,
            new { ClientMessageId = clientMessageId, actor.DisplayName },
            transaction,
            cancellationToken: cancellationToken));
        if (existing is not null)
        {
            var replayAttachments = _attachmentsEnabled
                ? await LoadAttachmentSummariesAsync(
                    connection,
                    transaction,
                    [existing.Id],
                    cancellationToken)
                : [];
            var replayAttachmentList = replayAttachments.GetValueOrDefault(existing.Id) ?? [];
            var replayAttachmentIds = replayAttachmentList
                .OrderBy(item => item.Position)
                .Select(item => item.Id)
                .ToArray();
            await transaction.RollbackAsync(CancellationToken.None);
            var sameActor = existing.SenderUserId == actor.UserId
                && existing.SenderKind == (actor.IsAdmin ? "Admin" : "Client");
            return sameActor
                   && existing.ConversationId == conversationId
                   && string.Equals(existing.Text, text, StringComparison.Ordinal)
                   && replayAttachmentIds.SequenceEqual(attachmentIds)
                ? new(
                    ConversationCommandStatus.Replayed,
                    ToMessage(
                        existing,
                        replayAttachments.GetValueOrDefault(existing.Id) ?? []))
                : new(ConversationCommandStatus.Conflict, ErrorCode: "idempotency_conflict");
        }

        var attachmentValidation = await ValidateAttachmentsForBindingAsync(
            connection,
            transaction,
            conversationId,
            actor,
            attachmentIds,
            cancellationToken);
        if (attachmentValidation.Status != ConversationCommandStatus.Success)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            return new(
                attachmentValidation.Status,
                ErrorCode: attachmentValidation.ErrorCode);
        }

        const string allocateSql = """
            UPDATE digital.conversation_sequences
            SET next_sequence = next_sequence + 1
            WHERE conversation_id = @ConversationId
            RETURNING next_sequence - 1;
            """;
        var sequence = await connection.QuerySingleOrDefaultAsync<long?>(new CommandDefinition(
            allocateSql,
            new { ConversationId = conversationId },
            transaction,
            cancellationToken: cancellationToken));
        if (sequence is null)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            return new(ConversationCommandStatus.Unavailable, ErrorCode: "conversation_unavailable");
        }

        var sentAt = DateTime.UtcNow;
        const string insertSql = """
            INSERT INTO digital.instructions (
                datetime, inst_category_id, inst_type_id, instruction,
                status, insert_user, client_auth_user_id, client_id,
                service_id, ip_address, inst_channel, instruction_id,
                client_message_id, conversation_sequence)
            SELECT @SentAt, root.inst_category_id, root.inst_type_id, @Text,
                   TRUE, @AdminUserId, @ClientUserId, root.client_id,
                   3, @IpAddress, 'chat', root.id,
                   @ClientMessageId, @Sequence
            FROM digital.instructions root
            WHERE root.id = @ConversationId
              AND root.instruction_id = root.id
            RETURNING id;
            """;
        var messageId = await connection.QuerySingleAsync<long>(new CommandDefinition(
            insertSql,
            new
            {
                ConversationId = conversationId,
                Text = text,
                SentAt = sentAt,
                IpAddress = ipAddress,
                ClientMessageId = clientMessageId,
                Sequence = sequence.Value,
                AdminUserId = actor.IsAdmin ? checked((int)actor.UserId) : (int?)null,
                ClientUserId = actor.IsAdmin ? (int?)null : checked((int)actor.UserId)
            },
            transaction,
            cancellationToken: cancellationToken));

        if (attachmentIds.Count > 0)
        {
            const string bindSql = """
                WITH requested AS (
                    SELECT attachment_id, ordinality::integer AS position
                    FROM unnest(@AttachmentIds::uuid[]) WITH ORDINALITY
                         AS selected(attachment_id, ordinality)
                ), bound AS (
                    UPDATE digital.attachments attachment
                    SET message_id = @MessageId,
                        position = requested.position,
                        bound_at = @SentAt,
                        expires_at = @SentAt + INTERVAL '365 days',
                        updated_at = @SentAt
                    FROM requested
                    WHERE attachment.id = requested.attachment_id
                      AND attachment.conversation_id = @ConversationId
                      AND attachment.state = 'Ready'
                      AND attachment.message_id IS NULL
                    RETURNING attachment.id
                )
                INSERT INTO digital.attachment_audit (
                    attachment_id, client_id, action, actor_kind,
                    admin_user_id, client_user_id, occurred_at, details)
                SELECT attachment.id,
                       attachment.client_id,
                       'BoundToMessage',
                       @ActorKind,
                       @AuditAdminUserId,
                       @AuditClientUserId,
                       @SentAt,
                       jsonb_build_object(
                           'conversationId', @ConversationId,
                           'messageId', @MessageId,
                           'position', attachment.position)
                FROM digital.attachments attachment
                JOIN bound ON bound.id = attachment.id;
                """;
            var boundCount = await connection.ExecuteAsync(new CommandDefinition(
                bindSql,
                new
                {
                    AttachmentIds = attachmentIds.ToArray(),
                    ConversationId = conversationId,
                    MessageId = messageId,
                    SentAt = sentAt,
                    ActorKind = actor.IsAdmin ? "Admin" : "Client",
                    AuditAdminUserId = actor.IsAdmin ? checked((int)actor.UserId) : (int?)null,
                    AuditClientUserId = actor.IsAdmin ? (int?)null : checked((int)actor.UserId)
                },
                transaction,
                cancellationToken: cancellationToken));
            if (boundCount != attachmentIds.Count)
            {
                await transaction.RollbackAsync(CancellationToken.None);
                return new(
                    ConversationCommandStatus.Conflict,
                    ErrorCode: "attachment_bind_conflict");
            }
        }

        var eventId = Guid.NewGuid();
        const string outboxSql = """
            INSERT INTO digital.conversation_outbox (
                event_id, conversation_id, client_id, conversation_kind,
                conversation_state, client_user_id, admin_user_id, access_version,
                message_id, event_type,
                schema_version, payload, occurred_at, available_at, attempt_count)
            SELECT @EventId, ca.conversation_id, ca.client_id, ca.conversation_kind,
                ca.state, ca.client_user_id, ca.admin_user_id, ca.version,
                @MessageId, 'MessageCreated',
                1,
                jsonb_build_object(
                    'eventId', @EventId,
                    'conversationId', @ConversationId,
                    'messageId', @MessageId,
                    'sequence', @Sequence),
                @OccurredAt, @OccurredAt, 0
            FROM digital.conversation_access ca
            WHERE ca.conversation_id = @ConversationId;
            """;
        await connection.ExecuteAsync(new CommandDefinition(
            outboxSql,
            new
            {
                EventId = eventId,
                ConversationId = conversationId,
                MessageId = messageId,
                Sequence = sequence.Value,
                OccurredAt = sentAt
            },
            transaction,
            cancellationToken: cancellationToken));
        await transaction.CommitAsync(cancellationToken);

        var createdAttachments = attachmentValidation.Value ?? [];
        return new(
            ConversationCommandStatus.Created,
            new ConversationMessage(
                messageId,
                conversationId,
                text,
                sentAt,
                new ConversationSender(
                    actor.UserId,
                    actor.DisplayName,
                    actor.IsAdmin ? "Admin" : "Client"),
                clientMessageId,
                sequence.Value,
                createdAttachments));
    }

    public async Task<ConversationCommandResult<long>> AdvanceReadCursorAsync(
        long conversationId,
        ConversationActor actor,
        long throughSequence,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var access = await GetAccessForUpdateAsync(
            connection,
            transaction,
            conversationId,
            actor,
            cancellationToken);
        if (access is null)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            return new(ConversationCommandStatus.Unavailable, ErrorCode: "conversation_unavailable");
        }

        var latest = await connection.QuerySingleAsync<long>(new CommandDefinition(
            """
            SELECT COALESCE(next_sequence - 1, 0)
            FROM digital.conversation_sequences
            WHERE conversation_id = @ConversationId;
            """,
            new { ConversationId = conversationId },
            transaction,
            cancellationToken: cancellationToken));
        if (throughSequence > latest)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            return new(ConversationCommandStatus.Conflict, ErrorCode: "read_cursor_ahead");
        }

        const string updateSql = """
            UPDATE digital.conversation_read_cursors
            SET last_read_sequence = GREATEST(last_read_sequence, @ThroughSequence),
                updated_at = @Now
            WHERE conversation_id = @ConversationId
              AND ((@IsAdmin AND admin_user_id = @UserId)
                   OR (NOT @IsAdmin AND client_user_id = @UserId));
            """;
        var parameters = ActorParameters(actor, new
        {
            ConversationId = conversationId,
            ThroughSequence = throughSequence,
            Now = DateTime.UtcNow
        });
        var updated = await connection.ExecuteAsync(new CommandDefinition(
            updateSql,
            parameters,
            transaction,
            cancellationToken: cancellationToken));
        if (updated == 0)
        {
            const string insertSql = """
                INSERT INTO digital.conversation_read_cursors (
                    conversation_id, principal_kind, admin_user_id, client_user_id,
                    last_read_sequence, updated_at)
                VALUES (
                    @ConversationId, @PrincipalKind, @AdminUserId, @ClientUserId,
                    @ThroughSequence, @Now)
                ON CONFLICT DO NOTHING;
                """;
            await connection.ExecuteAsync(new CommandDefinition(
                insertSql,
                new
                {
                    ConversationId = conversationId,
                    PrincipalKind = actor.IsAdmin ? "Admin" : "Client",
                    AdminUserId = actor.IsAdmin ? checked((int)actor.UserId) : (int?)null,
                    ClientUserId = actor.IsAdmin ? (int?)null : checked((int)actor.UserId),
                    ThroughSequence = throughSequence,
                    Now = DateTime.UtcNow
                },
                transaction,
                cancellationToken: cancellationToken));
            await connection.ExecuteAsync(new CommandDefinition(
                updateSql,
                parameters,
                transaction,
                cancellationToken: cancellationToken));
        }

        await transaction.CommitAsync(cancellationToken);
        return new(ConversationCommandStatus.Success, throughSequence);
    }

    public async Task<ConversationCommandResult<ConversationSummary>> TransferAsync(
        long conversationId,
        ConversationActor actor,
        long targetAdminUserId,
        long expectedVersion,
        string? reason,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await AcquireConversationMutationLockAsync(
            connection,
            transaction,
            conversationId,
            cancellationToken);

        const string accessSql = """
            SELECT conversation_id AS ConversationId,
                   client_id AS ClientId,
                   conversation_kind AS Kind,
                   state AS State,
                   client_user_id AS ClientUserId,
                   CAST(admin_user_id AS bigint) AS AdminUserId,
                   version AS Version
            FROM digital.conversation_access
            WHERE conversation_id = @ConversationId
            FOR UPDATE;
            """;
        var row = await connection.QuerySingleOrDefaultAsync<AccessRow>(new CommandDefinition(
            accessSql,
            new { ConversationId = conversationId },
            transaction,
            cancellationToken: cancellationToken));
        if (row is null || row.Kind != "Private" || row.State != "Active")
        {
            await transaction.RollbackAsync(CancellationToken.None);
            return new(ConversationCommandStatus.Unavailable, ErrorCode: "conversation_unavailable");
        }

        var assignedAdminActive = await connection.QuerySingleOrDefaultAsync<bool?>(new CommandDefinition(
            "SELECT status IS TRUE AND deactive_date IS NULL FROM admin.users WHERE id = @AdminUserId;",
            new { AdminUserId = row.AdminUserId },
            transaction,
            cancellationToken: cancellationToken)) ?? false;
        var assignedActor = row.AdminUserId == actor.UserId;
        if (!assignedActor && (assignedAdminActive || string.IsNullOrWhiteSpace(reason)))
        {
            await transaction.RollbackAsync(CancellationToken.None);
            return new(ConversationCommandStatus.Unavailable, ErrorCode: "conversation_unavailable");
        }

        var targetActive = await connection.QuerySingleOrDefaultAsync<bool?>(new CommandDefinition(
            "SELECT status IS TRUE AND deactive_date IS NULL FROM admin.users WHERE id = @AdminUserId;",
            new { AdminUserId = targetAdminUserId },
            transaction,
            cancellationToken: cancellationToken)) ?? false;
        if (!targetActive || row.Version != expectedVersion)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            return new(ConversationCommandStatus.Conflict, ErrorCode: "conversation_version_conflict");
        }

        var collision = await connection.QuerySingleOrDefaultAsync<long?>(new CommandDefinition(
            """
            SELECT conversation_id
            FROM digital.conversation_access
            WHERE conversation_kind = 'Private'
              AND state = 'Active'
              AND client_user_id = @ClientUserId
              AND admin_user_id = @AdminUserId
              AND conversation_id <> @ConversationId;
            """,
            new
            {
                row.ClientUserId,
                AdminUserId = checked((int)targetAdminUserId),
                ConversationId = conversationId
            },
            transaction,
            cancellationToken: cancellationToken));
        if (collision is not null)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            return new(ConversationCommandStatus.Conflict, ErrorCode: "private_pair_conflict");
        }

        await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE digital.conversation_access
            SET admin_user_id = @TargetAdminUserId,
                version = version + 1
            WHERE conversation_id = @ConversationId;
            """,
            new
            {
                ConversationId = conversationId,
                TargetAdminUserId = checked((int)targetAdminUserId)
            },
            transaction,
            cancellationToken: cancellationToken));
        await InsertAuditAsync(
            connection,
            transaction,
            conversationId,
            row.ClientId,
            assignedActor ? "Transferred" : "RecoveryTransferred",
            actor,
            new { previousAdminUserId = row.AdminUserId, targetAdminUserId, reason },
            cancellationToken);
        await InsertAccessChangedOutboxAsync(
            connection,
            transaction,
            conversationId,
            "ConversationTransferred",
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        var summary = await FindSummaryAsync(
            connection,
            null,
            actor,
            "ca.conversation_id = @ConversationId",
            new { ConversationId = conversationId },
            cancellationToken);
        return new(ConversationCommandStatus.Success, summary);
    }

    public async Task<ConversationCommandResult<ConversationSummary>> ArchiveAsync(
        long conversationId,
        ConversationActor actor,
        long expectedVersion,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await AcquireConversationMutationLockAsync(
            connection,
            transaction,
            conversationId,
            cancellationToken);
        var access = await GetAccessForUpdateAsync(
            connection,
            transaction,
            conversationId,
            actor,
            cancellationToken);
        if (access is null || !access.IsPrivate || access.Version != expectedVersion)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            return access is null
                ? new(ConversationCommandStatus.Unavailable, ErrorCode: "conversation_unavailable")
                : new(ConversationCommandStatus.Conflict, ErrorCode: "conversation_version_conflict");
        }

        var now = DateTime.UtcNow;
        await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE digital.conversation_access
            SET state = 'Archived', archived_at = @Now, version = version + 1
            WHERE conversation_id = @ConversationId;
            """,
            new { ConversationId = conversationId, Now = now },
            transaction,
            cancellationToken: cancellationToken));
        await InsertAuditAsync(
            connection,
            transaction,
            conversationId,
            access.ClientId!.Value,
            "Archived",
            actor,
            details: null,
            cancellationToken);
        await InsertAccessChangedOutboxAsync(
            connection,
            transaction,
            conversationId,
            "ConversationArchived",
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new(
            ConversationCommandStatus.Success,
            new ConversationSummary(
                conversationId,
                access.ClientId.Value,
                "Private",
                "Archived",
                access.ClientUserId,
                null,
                access.AdminUserId,
                null,
                0,
                0,
                0,
                now,
                access.Version + 1));
    }

    public async Task<ConversationCommandResult<ConversationSummary>> ApproveLegacyPrivateAsync(
        long conversationId,
        ConversationActor actor,
        int clientUserId,
        long adminUserId,
        long expectedVersion,
        string reason,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await AcquireConversationMutationLockAsync(
            connection,
            transaction,
            conversationId,
            cancellationToken);

        const string accessSql = """
            SELECT conversation_id AS ConversationId,
                   client_id AS ClientId,
                   conversation_kind AS Kind,
                   state AS State,
                   client_user_id AS ClientUserId,
                   CAST(admin_user_id AS bigint) AS AdminUserId,
                   version AS Version
            FROM digital.conversation_access
            WHERE conversation_id = @ConversationId
            FOR UPDATE;
            """;
        var row = await connection.QuerySingleOrDefaultAsync<AccessRow>(new CommandDefinition(
            accessSql,
            new { ConversationId = conversationId },
            transaction,
            cancellationToken: cancellationToken));
        if (row is null || row.Kind != "Private" || row.State != ConversationStates.NeedsReview)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            return new(ConversationCommandStatus.Unavailable, ErrorCode: "conversation_unavailable");
        }

        if (row.Version != expectedVersion)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            return new(ConversationCommandStatus.Conflict, ErrorCode: "conversation_version_conflict");
        }

        var clientUserValid = await connection.QuerySingleOrDefaultAsync<bool?>(new CommandDefinition(
            """
            SELECT TRUE
            FROM internal.support_users
            WHERE id = @ClientUserId
              AND client_id = @ClientId
              AND status IS TRUE
              AND deactive_date IS NULL;
            """,
            new { ClientUserId = clientUserId, row.ClientId },
            transaction,
            cancellationToken: cancellationToken)) ?? false;
        var adminUserValid = await connection.QuerySingleOrDefaultAsync<bool?>(new CommandDefinition(
            """
            SELECT TRUE
            FROM admin.users
            WHERE id = @AdminUserId
              AND status IS TRUE
              AND deactive_date IS NULL;
            """,
            new { AdminUserId = checked((int)adminUserId) },
            transaction,
            cancellationToken: cancellationToken)) ?? false;
        if (!clientUserValid || !adminUserValid)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            return new(ConversationCommandStatus.Unavailable, ErrorCode: "participant_unavailable");
        }

        var collision = await connection.QuerySingleOrDefaultAsync<long?>(new CommandDefinition(
            """
            SELECT conversation_id
            FROM digital.conversation_access
            WHERE conversation_kind = 'Private'
              AND state = 'Active'
              AND client_user_id = @ClientUserId
              AND admin_user_id = @AdminUserId
              AND conversation_id <> @ConversationId;
            """,
            new
            {
                ClientUserId = clientUserId,
                AdminUserId = checked((int)adminUserId),
                ConversationId = conversationId
            },
            transaction,
            cancellationToken: cancellationToken));
        if (collision is not null)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            return new(ConversationCommandStatus.Conflict, ErrorCode: "private_pair_conflict");
        }

        try
        {
            await connection.ExecuteAsync(new CommandDefinition(
                """
                UPDATE digital.conversation_access
                SET state = 'Active',
                    client_user_id = @ClientUserId,
                    admin_user_id = @AdminUserId,
                    version = version + 1
                WHERE conversation_id = @ConversationId;
                """,
                new
                {
                    ConversationId = conversationId,
                    ClientUserId = clientUserId,
                    AdminUserId = checked((int)adminUserId)
                },
                transaction,
                cancellationToken: cancellationToken));
            await InsertAuditAsync(
                connection,
                transaction,
                conversationId,
                row.ClientId,
                "LegacyPrivateApproved",
                actor,
                new { clientUserId, adminUserId, reason },
                cancellationToken);
            await InsertAccessChangedOutboxAsync(
                connection,
                transaction,
                conversationId,
                "ConversationApproved",
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            return new(ConversationCommandStatus.Conflict, ErrorCode: "private_pair_conflict");
        }

        var summary = await FindSummaryAsync(
            connection,
            null,
            actor,
            "ca.conversation_id = @ConversationId",
            new { ConversationId = conversationId },
            cancellationToken);
        return new(ConversationCommandStatus.Success, summary);
    }

    public async Task<IReadOnlyList<ConversationDirectoryUser>> GetAvailableAdminsAsync(
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT CAST(id AS bigint) AS Id,
                   COALESCE(full_name, user_name) AS DisplayName
            FROM admin.users
            WHERE status IS TRUE AND deactive_date IS NULL
            ORDER BY COALESCE(full_name, user_name), id
            LIMIT 200;
            """;
        await using var connection = new NpgsqlConnection(_connectionString);
        return (await connection.QueryAsync<ConversationDirectoryUser>(new CommandDefinition(
            sql,
            cancellationToken: cancellationToken))).AsList();
    }

    public async Task<IReadOnlyList<ConversationDirectoryUser>> GetAvailableClientUsersAsync(
        long clientId,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT CAST(id AS bigint) AS Id,
                   COALESCE(full_name, user_name) AS DisplayName
            FROM internal.support_users
            WHERE client_id = @ClientId
              AND status IS TRUE
              AND deactive_date IS NULL
            ORDER BY COALESCE(full_name, user_name), id
            LIMIT 500;
            """;
        await using var connection = new NpgsqlConnection(_connectionString);
        return (await connection.QueryAsync<ConversationDirectoryUser>(new CommandDefinition(
            sql,
            new { ClientId = clientId },
            cancellationToken: cancellationToken))).AsList();
    }

    private static object ActorParameters(ConversationActor actor, object? additional = null)
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

    private static Task<bool> IsClientActorInTenantAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        ConversationActor actor,
        long clientId,
        CancellationToken cancellationToken)
    {
        if (actor.IsAdmin)
        {
            return Task.FromResult(true);
        }

        return actor.UserId is > 0 and <= int.MaxValue
            && actor.ClientId == clientId
            ? IsSupportUserInTenantAsync(
                connection,
                transaction,
                checked((int)actor.UserId),
                clientId,
                cancellationToken)
            : Task.FromResult(false);
    }

    private static Task<bool> IsSupportUserInTenantAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        int clientUserId,
        long clientId,
        CancellationToken cancellationToken) =>
        connection.QuerySingleAsync<bool>(new CommandDefinition(
            """
            SELECT EXISTS (
                SELECT 1
                FROM internal.support_users
                WHERE id = @ClientUserId
                  AND client_id = @ClientId
                  AND status IS TRUE
                  AND deactive_date IS NULL);
            """,
            new { ClientUserId = clientUserId, ClientId = clientId },
            transaction,
            cancellationToken: cancellationToken));

    private static Task AcquireConversationMutationLockAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long conversationId,
        CancellationToken cancellationToken) =>
        connection.ExecuteAsync(new CommandDefinition(
            "SELECT pg_advisory_xact_lock(hashtextextended('cbs-support:conversation:' || @ConversationId, 0));",
            new { ConversationId = conversationId },
            transaction,
            cancellationToken: cancellationToken));

    private static async Task<ConversationAccess?> GetAccessForUpdateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long conversationId,
        ConversationActor actor,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT ca.conversation_id AS ConversationId,
                   ca.client_id AS ClientId,
                   root.inst_type_id AS InstructionTypeId,
                   root.inst_category_id AS InstructionCategoryId,
                   ca.state AS State,
                   ca.client_user_id AS ClientUserId,
                   CAST(ca.admin_user_id AS bigint) AS AdminUserId,
                   ca.version AS Version
            FROM digital.conversation_access ca
            JOIN digital.instructions root ON root.id = ca.conversation_id
            WHERE ca.conversation_id = @ConversationId
              AND ca.state = 'Active'
              AND (@IsAdmin OR EXISTS (
                    SELECT 1
                    FROM internal.support_users authenticated_client
                    WHERE authenticated_client.id = @UserId
                      AND authenticated_client.client_id = ca.client_id
                      AND authenticated_client.client_id = @ClientId
                      AND authenticated_client.status IS TRUE
                      AND authenticated_client.deactive_date IS NULL))
              AND (
                    (@IsAdmin AND (
                        ca.conversation_kind IN ('Group', 'Ticket', 'Inquiry')
                        OR ca.admin_user_id = @UserId))
                 OR (NOT @IsAdmin
                     AND ca.client_id = @ClientId
                     AND (
                        ca.conversation_kind IN ('Group', 'Ticket', 'Inquiry')
                        OR ca.client_user_id = @UserId))
              )
            FOR UPDATE OF ca;
            """;
        return await connection.QuerySingleOrDefaultAsync<ConversationAccess>(new CommandDefinition(
            sql,
            ActorParameters(actor, new { ConversationId = conversationId }),
            transaction,
            cancellationToken: cancellationToken));
    }

    private static async Task<ConversationSummary?> FindSummaryAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        ConversationActor actor,
        string predicate,
        object predicateParameters,
        CancellationToken cancellationToken)
    {
        var sql = SummaryColumns + "\nWHERE " + predicate + "\nLIMIT 1;";
        var parameters = new DynamicParameters(predicateParameters);
        parameters.Add("IsAdmin", actor.IsAdmin);
        parameters.Add("UserId", actor.UserId);
        parameters.Add("ClientId", actor.ClientId);
        return await connection.QuerySingleOrDefaultAsync<ConversationSummary>(new CommandDefinition(
            sql,
            parameters,
            transaction,
            cancellationToken: cancellationToken));
    }

    private static async Task InsertAuditAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long conversationId,
        long clientId,
        string action,
        ConversationActor actor,
        object? details,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO digital.conversation_audit (
                conversation_id, client_id, action, actor_kind,
                admin_user_id, client_user_id, occurred_at, details)
            VALUES (
                @ConversationId, @ClientId, @Action, @ActorKind,
                @AdminUserId, @ClientUserId, @OccurredAt, CAST(@Details AS jsonb));
            """;
        await connection.ExecuteAsync(new CommandDefinition(
            sql,
            new
            {
                ConversationId = conversationId,
                ClientId = clientId,
                Action = action,
                ActorKind = actor.IsAdmin ? "Admin" : "Client",
                AdminUserId = actor.IsAdmin ? checked((int)actor.UserId) : (int?)null,
                ClientUserId = actor.IsAdmin ? (int?)null : checked((int)actor.UserId),
                OccurredAt = DateTime.UtcNow,
                Details = JsonSerializer.Serialize(details ?? new { })
            },
            transaction,
            cancellationToken: cancellationToken));
    }

    private static async Task InsertAccessChangedOutboxAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long conversationId,
        string eventType,
        CancellationToken cancellationToken)
    {
        var eventId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        const string sql = """
            INSERT INTO digital.conversation_outbox (
                event_id, conversation_id, client_id, conversation_kind,
                conversation_state, client_user_id, admin_user_id, access_version,
                message_id, event_type,
                schema_version, payload, occurred_at, available_at, attempt_count)
            SELECT @EventId, ca.conversation_id, ca.client_id, ca.conversation_kind,
                ca.state, ca.client_user_id, ca.admin_user_id, ca.version,
                NULL, @EventType,
                1,
                jsonb_build_object('eventId', @EventId, 'conversationId', @ConversationId),
                @Now, @Now, 0
            FROM digital.conversation_access ca
            WHERE ca.conversation_id = @ConversationId;
            """;
        await connection.ExecuteAsync(new CommandDefinition(
            sql,
            new { EventId = eventId, ConversationId = conversationId, EventType = eventType, Now = now },
            transaction,
            cancellationToken: cancellationToken));
    }

    private static async Task<ConversationCommandResult<IReadOnlyList<AttachmentSummary>>>
        ValidateAttachmentsForBindingAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            long conversationId,
            ConversationActor actor,
            IReadOnlyList<Guid> attachmentIds,
            CancellationToken cancellationToken)
    {
        if (attachmentIds.Count == 0)
        {
            return new(ConversationCommandStatus.Success, []);
        }

        const string sql = """
            SELECT id AS Id,
                   display_name AS DisplayName,
                   COALESCE(detected_media_type, declared_media_type) AS MediaType,
                   COALESCE(actual_size, declared_size) AS Size,
                   state AS Status,
                   rejection_code AS RejectionCode,
                   position AS Position,
                   message_id AS MessageId
            FROM digital.attachments
            WHERE id = ANY(@AttachmentIds)
              AND conversation_id = @ConversationId
              AND (
                    (@IsAdmin AND admin_user_id = @UserId)
                    OR (NOT @IsAdmin
                        AND client_id = @ClientId
                        AND client_user_id = @UserId)
              )
            FOR UPDATE;
            """;
        var rows = (await connection.QueryAsync<AttachmentBindingRow>(new CommandDefinition(
            sql,
            ActorParameters(actor, new
            {
                ConversationId = conversationId,
                AttachmentIds = attachmentIds.ToArray()
            }),
            transaction,
            cancellationToken: cancellationToken))).AsList();
        if (rows.Count != attachmentIds.Count)
        {
            return new(ConversationCommandStatus.Unavailable, ErrorCode: "attachment_not_found");
        }

        var byId = rows.ToDictionary(row => row.Id);
        var ordered = attachmentIds.Select(id => byId[id]).ToArray();
        var invalid = ordered.FirstOrDefault(row =>
            !string.Equals(row.Status, AttachmentStates.Ready, StringComparison.Ordinal)
            || row.MessageId is not null);
        if (invalid is not null)
        {
            var code = invalid.MessageId is not null
                ? "attachment_already_bound"
                : invalid.Status switch
                {
                    AttachmentStates.Rejected => "attachment_rejected",
                    AttachmentStates.ScanFailed => "attachment_scan_failed",
                    AttachmentStates.Expired => "attachment_expired",
                    _ => "attachment_not_ready"
                };
            return new(ConversationCommandStatus.Conflict, ErrorCode: code);
        }

        if (ordered.Sum(row => row.Size) > 25L * 1024 * 1024)
        {
            return new(
                ConversationCommandStatus.Conflict,
                ErrorCode: "attachment_message_size_exceeded");
        }

        return new(
            ConversationCommandStatus.Success,
            ordered.Select((row, index) => new AttachmentSummary(
                row.Id,
                row.DisplayName,
                row.MediaType,
                row.Size,
                row.Status,
                row.RejectionCode,
                index + 1)).ToArray());
    }

    private static async Task<Dictionary<long, IReadOnlyList<AttachmentSummary>>>
        LoadAttachmentSummariesAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction? transaction,
            IReadOnlyList<long> messageIds,
            CancellationToken cancellationToken)
    {
        if (messageIds.Count == 0)
        {
            return [];
        }

        const string sql = """
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
            """;
        var rows = await connection.QueryAsync<AttachmentMessageRow>(new CommandDefinition(
            sql,
            new { MessageIds = messageIds.ToArray() },
            transaction,
            cancellationToken: cancellationToken));
        return rows
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
    }

    private static ConversationMessage ToMessage(
        MessageRow row,
        IReadOnlyList<AttachmentSummary> attachments) =>
        new(
            row.Id,
            row.ConversationId,
            row.Text,
            row.SentAt,
            new ConversationSender(row.SenderUserId, row.SenderDisplayName, row.SenderKind),
            row.ClientMessageId,
            row.Sequence,
            attachments);

    private sealed record MessageRow(
        long Id,
        long ConversationId,
        string? Text,
        DateTime SentAt,
        long SenderUserId,
        string SenderKind,
        string SenderDisplayName,
        Guid? ClientMessageId,
        long Sequence);

    private sealed record AttachmentBindingRow(
        Guid Id,
        string DisplayName,
        string MediaType,
        long Size,
        string Status,
        string? RejectionCode,
        short? Position,
        long? MessageId);

    private sealed record AttachmentMessageRow(
        long MessageId,
        Guid Id,
        string DisplayName,
        string MediaType,
        long Size,
        string Status,
        string? RejectionCode,
        short Position);

    private sealed record AccessRow(
        long ConversationId,
        long ClientId,
        string Kind,
        string State,
        int? ClientUserId,
        long? AdminUserId,
        long Version);
}
