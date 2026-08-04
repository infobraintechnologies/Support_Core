using CBSSupport.Shared.Contracts;
using CBSSupport.Shared.Services;
using Dapper;
using Npgsql;

namespace CBSSupport.API.Tests.Integration;

public sealed class CaseAttachmentPostgreSqlIntegrationTests
{
    [PostgreSqlIntegrationFact]
    public async Task CaseMigration_BackfillsRootRepliesAndAllocatorDeterministically()
    {
        await using var database = await TestDatabase.CreateAsync();
        await database.InitializeMessagingSchemaAsync();
        await database.ExecuteAsync("""
            INSERT INTO digital.instructions (
                id, datetime, insert_date, inst_category_id, inst_type_id,
                instruction, status, client_auth_user_id, client_id,
                service_id, inst_channel, instruction_id)
            VALUES
                (1100, '2026-07-01T00:00:00Z', '2026-07-01T00:00:00Z',
                 101, 110, 'Root', TRUE, 7, 42, 3, 'chat', 1100),
                (1101, '2026-07-01T00:03:00Z', '2026-07-01T00:03:00Z',
                 100, 100, 'Third', TRUE, 7, 42, 3, 'chat', 1100),
                (1102, NULL, '2026-07-01T00:01:00Z',
                 101, 100, 'First', TRUE, 7, 42, 3, 'chat', 1100),
                (1103, '2026-07-01T00:01:00Z', '2026-07-01T00:02:00Z',
                 100, 110, 'Second', TRUE, 7, 42, 3, 'chat', 1100);
            """);

        await database.ApplyMigrationAsync(
            "202607261005_normalize_legacy_case_reply_shape.sql");
        await database.ApplyMigrationAsync("202607261010_modernize_case_conversations.sql");

        var rows = (await database.QueryAsync<SequenceRow>("""
            SELECT id, conversation_sequence AS Sequence
            FROM digital.instructions
            WHERE instruction_id = 1100
            ORDER BY conversation_sequence;
            """)).ToArray();
        Assert.Equal(
            [
                new SequenceRow(1100, 1),
                new SequenceRow(1102, 2),
                new SequenceRow(1103, 3),
                new SequenceRow(1101, 4)
            ],
            rows);
        Assert.Equal(5L, await database.QuerySingleAsync<long>("""
            SELECT next_sequence
            FROM digital.conversation_sequences
            WHERE conversation_id = 1100;
            """));
        Assert.Equal(0, await database.QuerySingleAsync<int>("""
            SELECT count(*)
            FROM digital.instructions
            WHERE instruction_id = 1100
              AND (
                    inst_type_id IS DISTINCT FROM 110
                    OR inst_category_id IS DISTINCT FROM 101
              );
            """));
        Assert.Equal("Ticket", await database.QuerySingleAsync<string>("""
            SELECT conversation_kind
            FROM digital.conversation_access
            WHERE conversation_id = 1100;
            """));
    }

    [PostgreSqlIntegrationFact]
    public async Task CaseReplyNormalization_AmbiguousShapeFailsWithoutMutation()
    {
        await using var database = await TestDatabase.CreateAsync();
        await database.InitializeMessagingSchemaAsync();
        await database.ExecuteAsync("""
            INSERT INTO digital.instructions (
                id, datetime, inst_category_id, inst_type_id,
                instruction, status, client_auth_user_id, client_id,
                service_id, inst_channel, instruction_id)
            VALUES
                (1200, now(), 101, 110, 'Root', TRUE, 7, 42, 3, 'chat', 1200),
                (1201, now(), 100, 100, 'Repairable', TRUE, 7, 42, 3, 'chat', 1200),
                (1202, now(), 101, 111, 'Ambiguous', TRUE, 7, 42, 3, 'chat', 1200);
            """);

        var exception = await Assert.ThrowsAsync<PostgresException>(() =>
            database.ApplyMigrationAsync(
                "202607261005_normalize_legacy_case_reply_shape.sql"));

        Assert.Contains(
            "ambiguous type/category mismatch",
            exception.Message,
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal((short)100, await database.QuerySingleAsync<short>("""
            SELECT inst_type_id
            FROM digital.instructions
            WHERE id = 1201;
            """));
        Assert.Equal((short)100, await database.QuerySingleAsync<short>("""
            SELECT inst_category_id
            FROM digital.instructions
            WHERE id = 1201;
            """));
    }

    [PostgreSqlIntegrationFact]
    public async Task CaseRootAndReply_AreAtomicSequencedIdempotentAndOutboxed()
    {
        await using var database = await TestDatabase.CreateAsync();
        await database.InitializeMessagingSchemaAsync();
        await database.ApplyCaseAndAttachmentMigrationsAsync();
        var repository = new ConversationRepository(
            database.ConnectionString,
            attachmentsEnabled: true);
        var actor = new ConversationActor(7, 42, IsAdmin: false, "Client User");

        var root = await repository.CreateCaseAsync(
            actor,
            ConversationTypes.TrainingTicket,
            InstructionCategories.Ticket,
            "Root text",
            """{"priority":"Normal","userremarks":"","subject":"Training"}""",
            null,
            "127.0.0.1",
            DateTime.UtcNow);
        Assert.Equal(ConversationCommandStatus.Created, root.Status);
        Assert.NotNull(root.Value);
        var conversationId = root.Value.Id;
        Assert.Equal(1, root.Value.ConversationSequence);
        Assert.Null(await database.QuerySingleAsync<int?>(
            "SELECT insert_user FROM digital.instructions WHERE id = @Id;",
            new { Id = conversationId }));
        Assert.Equal(7, await database.QuerySingleAsync<int?>(
            "SELECT client_auth_user_id FROM digital.instructions WHERE id = @Id;",
            new { Id = conversationId }));
        Assert.Equal(2L, await database.QuerySingleAsync<long>(
            "SELECT next_sequence FROM digital.conversation_sequences WHERE conversation_id = @Id;",
            new { Id = conversationId }));

        var clientMessageId = Guid.NewGuid();
        var created = await repository.SendMessageAsync(
            conversationId,
            actor,
            clientMessageId,
            "Reply",
            [],
            null);
        var replay = await repository.SendMessageAsync(
            conversationId,
            actor,
            clientMessageId,
            "Reply",
            [],
            null);

        Assert.Equal(ConversationCommandStatus.Created, created.Status);
        Assert.Equal(ConversationCommandStatus.Replayed, replay.Status);
        Assert.Equal(created.Value?.Id, replay.Value?.Id);
        Assert.Equal(2L, created.Value?.Sequence);
        Assert.Equal(2, await database.QuerySingleAsync<int>("""
            SELECT count(*)
            FROM digital.conversation_outbox
            WHERE conversation_id = @Id AND event_type = 'MessageCreated';
            """, new { Id = conversationId }));

        await database.ExecuteAsync("""
            CREATE FUNCTION digital.reject_test_outbox() RETURNS trigger
            LANGUAGE plpgsql AS $$
            BEGIN
                IF NEW.message_id <> NEW.conversation_id THEN
                    RAISE EXCEPTION 'test outbox failure';
                END IF;
                RETURN NEW;
            END $$;
            CREATE TRIGGER reject_test_outbox
            BEFORE INSERT ON digital.conversation_outbox
            FOR EACH ROW EXECUTE FUNCTION digital.reject_test_outbox();
            """);

        await Assert.ThrowsAsync<PostgresException>(() => repository.SendMessageAsync(
            conversationId,
            actor,
            Guid.NewGuid(),
            "Must roll back",
            [],
            null));
        Assert.Equal(3L, await database.QuerySingleAsync<long>(
            "SELECT next_sequence FROM digital.conversation_sequences WHERE conversation_id = @Id;",
            new { Id = conversationId }));
        Assert.Equal(0, await database.QuerySingleAsync<int>("""
            SELECT count(*) FROM digital.instructions
            WHERE instruction_id = @Id AND instruction = 'Must roll back';
            """, new { Id = conversationId }));
    }

    [PostgreSqlIntegrationFact]
    public async Task CreateCase_ClientIdentityFromAnotherTenant_IsRejectedWithoutInsert()
    {
        await using var database = await TestDatabase.CreateAsync();
        await database.InitializeMessagingSchemaAsync();
        await database.ApplyCaseAndAttachmentMigrationsAsync();
        var repository = new ConversationRepository(
            database.ConnectionString,
            attachmentsEnabled: true);

        var result = await repository.CreateCaseAsync(
            new ConversationActor(7, 43, IsAdmin: false, "Spoofed Client"),
            ConversationTypes.TrainingTicket,
            InstructionCategories.Ticket,
            "Must not be inserted",
            null,
            null,
            null,
            DateTime.UtcNow);

        Assert.Equal(ConversationCommandStatus.Unavailable, result.Status);
        Assert.Equal("client_identity_unavailable", result.ErrorCode);
        Assert.Equal(0, await database.QuerySingleAsync<int>(
            "SELECT count(*) FROM digital.instructions WHERE instruction = 'Must not be inserted';"));
    }

    [PostgreSqlIntegrationFact]
    public async Task Phase1TextMessaging_AttachmentsDisabled_DoesNotRequireAttachmentSchema()
    {
        await using var database = await TestDatabase.CreateAsync();
        await database.InitializeMessagingSchemaAsync();
        await database.ApplyMigrationAsync(
            "202607261005_normalize_legacy_case_reply_shape.sql");
        await database.ApplyMigrationAsync(
            "202607261010_modernize_case_conversations.sql");
        var repository = new ConversationRepository(
            database.ConnectionString,
            attachmentsEnabled: false);
        var actor = new ConversationActor(7, 42, IsAdmin: false, "Client User");

        var root = await repository.CreateCaseAsync(
            actor,
            ConversationTypes.TrainingTicket,
            InstructionCategories.Ticket,
            "Phase 1 root",
            """{"priority":"Normal","userremarks":"","subject":"Training"}""",
            null,
            "127.0.0.1",
            DateTime.UtcNow);
        Assert.Equal(ConversationCommandStatus.Created, root.Status);
        Assert.NotNull(root.Value);

        var clientMessageId = Guid.NewGuid();
        var created = await repository.SendMessageAsync(
            root.Value.Id,
            actor,
            clientMessageId,
            "Text-only reply",
            [],
            null);
        var replay = await repository.SendMessageAsync(
            root.Value.Id,
            actor,
            clientMessageId,
            "Text-only reply",
            [],
            null);
        var page = await repository.GetMessagesAsync(
            root.Value.Id,
            actor,
            20,
            null,
            null);

        Assert.Equal(ConversationCommandStatus.Created, created.Status);
        Assert.Equal(ConversationCommandStatus.Replayed, replay.Status);
        Assert.Equal(created.Value?.Id, replay.Value?.Id);
        Assert.NotNull(page);
        Assert.Equal(2, page.Items.Count);
        Assert.All(page.Items, message => Assert.Empty(message.SafeAttachments));

        var outbox = new ConversationOutboxRepository(
            database.ConnectionString,
            attachmentsEnabled: false);
        var claimed = await outbox.ClaimAsync(
            "phase1-test",
            10,
            DateTime.UtcNow.AddMinutes(1),
            DateTime.UtcNow.AddMinutes(2));
        Assert.Equal(2, claimed.Count);
        Assert.Contains(
            claimed,
            item => item.Message?.Text == "Text-only reply");
        Assert.All(
            claimed.Where(item => item.Message is not null),
            item => Assert.Empty(item.Message!.SafeAttachments));

        var allocatorBefore = await database.QuerySingleAsync<long>(
            "SELECT next_sequence FROM digital.conversation_sequences WHERE conversation_id = @Id;",
            new { Id = root.Value.Id });
        var disabledAttachment = await repository.SendMessageAsync(
            root.Value.Id,
            actor,
            Guid.NewGuid(),
            null,
            [Guid.NewGuid()],
            null);
        var allocatorAfter = await database.QuerySingleAsync<long>(
            "SELECT next_sequence FROM digital.conversation_sequences WHERE conversation_id = @Id;",
            new { Id = root.Value.Id });

        Assert.Equal(ConversationCommandStatus.Conflict, disabledAttachment.Status);
        Assert.Equal("attachments_disabled", disabledAttachment.ErrorCode);
        Assert.Equal(allocatorBefore, allocatorAfter);
    }

    [PostgreSqlIntegrationFact]
    public async Task AttachmentCompositeConstraints_RejectCrossTenantAndCrossConversationBinding()
    {
        await using var database = await TestDatabase.CreateAsync();
        await database.InitializeMessagingSchemaAsync();
        await database.ApplyCaseAndAttachmentMigrationsAsync();
        await database.SeedGroupConversationsAsync();

        await Assert.ThrowsAsync<PostgresException>(() => database.ExecuteAsync(
            PendingAttachmentInsert,
            AttachmentParameters(Guid.NewGuid(), 43, 1000, null, null, 8)));
        await Assert.ThrowsAsync<PostgresException>(() => database.ExecuteAsync(
            PendingAttachmentInsert,
            AttachmentParameters(Guid.NewGuid(), 42, 1000, 1002, 1, 7)));
        await Assert.ThrowsAsync<PostgresException>(() => database.ExecuteAsync(
            PendingAttachmentInsert,
            AttachmentParameters(Guid.NewGuid(), 42, 1000, null, null, 8)));
    }

    [PostgreSqlIntegrationFact]
    public async Task AttachmentQuota_ConcurrentIntentsSerializeRollingAndTenantLimits()
    {
        await using var database = await TestDatabase.CreateAsync();
        await database.InitializeMessagingSchemaAsync();
        await database.ApplyCaseAndAttachmentMigrationsAsync();
        await database.SeedGroupConversationsAsync();
        var repository = new AttachmentRepository(database.ConnectionString);
        var now = DateTimeOffset.UtcNow;
        var rollingOptions = new AttachmentOptions
        {
            MaximumConcurrentUnboundPerUser = 1000,
            MaximumUserBytesPerRollingDay = 10L * 1024 * 1024
        };

        var rollingResults = await Task.WhenAll(
            repository.CreateIntentAsync(
                Intent(1000, 7, 42, 6L * 1024 * 1024, now, "rolling-a"),
                rollingOptions),
            repository.CreateIntentAsync(
                Intent(1000, 7, 42, 6L * 1024 * 1024, now, "rolling-b"),
                rollingOptions));
        Assert.Equal(1, rollingResults.Count(result =>
            result.Status == AttachmentCommandStatus.Accepted));
        Assert.Equal(1, rollingResults.Count(result =>
            result.ErrorCode == "attachment_user_daily_quota"));

        await database.ExecuteAsync(
            "DELETE FROM digital.attachment_audit; DELETE FROM digital.attachments;");
        await database.ExecuteAsync("""
            INSERT INTO digital.attachment_tenant_quotas (
                client_id, active_storage_limit_bytes)
            VALUES (42, 1073741824);
            """);
        for (var index = 0; index < 102; index++)
        {
            var size = index == 101 ? 8L * 1024 * 1024 : 10L * 1024 * 1024;
            await database.ExecuteAsync(
                PendingAttachmentInsert,
                AttachmentParameters(Guid.NewGuid(), 42, 1000, null, null, 7, size));
        }

        var tenantOptions = new AttachmentOptions
        {
            MaximumConcurrentUnboundPerUser = 1000,
            MaximumUserBytesPerRollingDay = long.MaxValue
        };
        var tenantResults = await Task.WhenAll(
            repository.CreateIntentAsync(
                Intent(1000, 7, 42, 4L * 1024 * 1024, now, "tenant-a"),
                tenantOptions),
            repository.CreateIntentAsync(
                Intent(1000, 7, 42, 4L * 1024 * 1024, now, "tenant-b"),
                tenantOptions));
        Assert.Equal(1, tenantResults.Count(result =>
            result.Status == AttachmentCommandStatus.Accepted));
        Assert.Equal(1, tenantResults.Count(result =>
            result.ErrorCode == "attachment_tenant_storage_quota"));
    }

    [PostgreSqlIntegrationFact]
    public async Task AttachmentIntent_AdminActor_UsesConversationTenantForAccessAndQuota()
    {
        await using var database = await TestDatabase.CreateAsync();
        await database.InitializeMessagingSchemaAsync();
        await database.ApplyCaseAndAttachmentMigrationsAsync();
        await database.SeedGroupConversationsAsync();
        var repository = new AttachmentRepository(database.ConnectionString);
        var now = DateTimeOffset.UtcNow;
        var attachmentId = Guid.NewGuid();

        var result = await repository.CreateIntentAsync(
            new AttachmentIntentRecord(
                attachmentId,
                42,
                1000,
                new AttachmentActor(9, ClientId: null, IsAdmin: true),
                $"quarantine/admin-{attachmentId:N}",
                "admin.txt",
                "text/plain",
                1024,
                now),
            new AttachmentOptions());

        Assert.Equal(AttachmentCommandStatus.Accepted, result.Status);
        Assert.Equal(42, result.Value?.ClientId);
        Assert.Equal(9, result.Value?.AdminUserId);
        Assert.Null(result.Value?.ClientUserId);
    }

    [PostgreSqlIntegrationFact]
    public async Task AttachmentRetention_ClaimsThenFinalizesWithoutEarlyQuotaRelease()
    {
        await using var database = await TestDatabase.CreateAsync();
        await database.InitializeMessagingSchemaAsync();
        await database.ApplyCaseAndAttachmentMigrationsAsync();
        await database.SeedGroupConversationsAsync();
        var repository = new AttachmentRepository(database.ConnectionString);
        var now = DateTimeOffset.UtcNow;
        var pendingId = Guid.NewGuid();
        var readyId = Guid.NewGuid();
        var boundId = Guid.NewGuid();

        await database.ExecuteAsync(
            PendingAttachmentInsert,
            AttachmentParameters(
                pendingId,
                42,
                1000,
                null,
                null,
                7,
                createdAt: now.AddHours(-2)));
        await database.ExecuteAsync(ReadyAttachmentInsert, new
        {
            Id = readyId,
            ClientId = 42,
            ConversationId = 1000,
            ClientUserId = 7,
            MessageId = (long?)null,
            Position = (short?)null,
            BoundAt = (DateTimeOffset?)null,
            ReadyAt = now.AddHours(-25),
            ExpiresAt = now.AddMinutes(-1)
        });
        await database.ExecuteAsync(ReadyAttachmentInsert, new
        {
            Id = boundId,
            ClientId = 42,
            ConversationId = 1000,
            ClientUserId = 7,
            MessageId = (long?)1000,
            Position = (short?)1,
            BoundAt = (DateTimeOffset?)now.AddDays(-366),
            ReadyAt = now.AddDays(-366),
            ExpiresAt = now.AddDays(-1)
        });

        var claimed = await repository.ClaimCleanupBatchAsync(
            "cleanup-test",
            10,
            now,
            now.AddMinutes(1));
        Assert.Equal(3, claimed.Count);
        Assert.All(claimed, row => Assert.Equal(AttachmentStates.DeletePending, row.State));
        Assert.All(claimed, row => Assert.True(row.ReservationBytes > 0));
        Assert.Equal(
            AttachmentRejectionCodes.UploadAbandoned,
            claimed.Single(row => row.Id == pendingId).RejectionCode);
        Assert.Equal(
            AttachmentStates.Expired,
            claimed.Single(row => row.Id == readyId).DeleteTargetState);

        foreach (var row in claimed)
        {
            await repository.FinalizeDeletionAsync(
                row.Id,
                "cleanup-test",
                row.DeleteTargetState!,
                row.RejectionCode,
                now);
        }
        Assert.Equal(0L, await database.QuerySingleAsync<long>("""
            SELECT COALESCE(sum(reservation_bytes), 0)
            FROM digital.attachments
            WHERE id = ANY(@Ids);
            """, new { Ids = new[] { pendingId, readyId, boundId } }));
    }

    private const string PendingAttachmentInsert = """
        INSERT INTO digital.attachments (
            id, client_id, conversation_id, message_id, position,
            client_user_id, state, quarantine_key,
            display_name, declared_media_type, declared_size,
            reservation_bytes, bound_at, created_at, updated_at, next_attempt_at)
        VALUES (
            @Id, @ClientId, @ConversationId, @MessageId, @Position,
            @ClientUserId, 'PendingUpload', 'quarantine/' || @Id,
            'file.txt', 'text/plain', @Size,
            @Size, CASE WHEN @MessageId IS NULL THEN NULL ELSE @CreatedAt END,
            @CreatedAt, @CreatedAt, @CreatedAt);
        """;

    private const string ReadyAttachmentInsert = """
        INSERT INTO digital.attachments (
            id, client_id, conversation_id, message_id, position,
            client_user_id, state, quarantine_key, ready_key,
            display_name, declared_media_type, detected_media_type,
            declared_size, actual_size, reservation_bytes,
            source_etag, expected_ready_etag, sha256,
            ready_at, bound_at, expires_at, created_at, updated_at, next_attempt_at)
        VALUES (
            @Id, @ClientId, @ConversationId, @MessageId, @Position,
            @ClientUserId, 'Ready', 'quarantine/' || @Id, 'ready/' || @Id,
            'file.txt', 'text/plain', 'text/plain',
            1024, 1024, 1024,
            'source-etag', 'ready-etag', decode(repeat('00', 32), 'hex'),
            @ReadyAt, @BoundAt, @ExpiresAt, @ReadyAt, @ReadyAt, @ReadyAt);
        """;

    private static object AttachmentParameters(
        Guid id,
        long clientId,
        long conversationId,
        long? messageId,
        short? position,
        int clientUserId,
        long size = 1024,
        DateTimeOffset? createdAt = null) =>
        new
        {
            Id = id,
            ClientId = clientId,
            ConversationId = conversationId,
            MessageId = messageId,
            Position = position,
            ClientUserId = clientUserId,
            Size = size,
            CreatedAt = createdAt ?? DateTimeOffset.UtcNow
        };

    private static AttachmentIntentRecord Intent(
        long conversationId,
        long userId,
        long clientId,
        long size,
        DateTimeOffset now,
        string suffix) =>
        new(
            Guid.NewGuid(),
            clientId,
            conversationId,
            new AttachmentActor(userId, clientId, IsAdmin: false),
            $"quarantine/{suffix}-{Guid.NewGuid():N}",
            $"{suffix}.txt",
            "text/plain",
            size,
            now);

    private sealed record SequenceRow(long Id, long Sequence);

    private sealed class TestDatabase : IAsyncDisposable
    {
        private readonly string _adminConnectionString;
        private readonly string _databaseName;

        private TestDatabase(
            string adminConnectionString,
            string databaseName,
            string connectionString)
        {
            _adminConnectionString = adminConnectionString;
            _databaseName = databaseName;
            ConnectionString = connectionString;
        }

        public string ConnectionString { get; }

        public static async Task<TestDatabase> CreateAsync()
        {
            var configured = Environment.GetEnvironmentVariable(
                PostgreSqlIntegrationFactAttribute.ConnectionStringEnvironmentVariable)!;
            var admin = new NpgsqlConnectionStringBuilder(configured) { Pooling = false };
            if (string.IsNullOrWhiteSpace(admin.Database))
            {
                admin.Database = "postgres";
            }
            var databaseName = $"cbssupport_data_{Guid.NewGuid():N}";
            await using (var connection = new NpgsqlConnection(admin.ConnectionString))
            {
                await connection.OpenAsync();
                await connection.ExecuteAsync($"CREATE DATABASE \"{databaseName}\"");
            }
            var application = new NpgsqlConnectionStringBuilder(admin.ConnectionString)
            {
                Database = databaseName,
                Pooling = false
            };
            return new TestDatabase(
                admin.ConnectionString,
                databaseName,
                application.ConnectionString);
        }

        public async Task InitializeMessagingSchemaAsync()
        {
            await ExecuteAsync("""
                CREATE SCHEMA admin;
                CREATE SCHEMA internal;
                CREATE SCHEMA digital;

                CREATE TABLE admin.users (
                    id integer PRIMARY KEY,
                    user_name text,
                    full_name text
                );
                CREATE TABLE internal.support_users (
                    id integer PRIMARY KEY,
                    client_id integer NOT NULL,
                    user_name text,
                    full_name text,
                    status boolean NOT NULL DEFAULT TRUE,
                    deactive_date timestamptz
                );
                INSERT INTO admin.users VALUES (9, 'admin', 'Admin');
                INSERT INTO internal.support_users VALUES
                    (7, 42, 'client-42', 'Client 42'),
                    (8, 43, 'client-43', 'Client 43');

                CREATE TABLE digital.instructions (
                    id bigint GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
                    datetime timestamptz,
                    insert_date timestamptz NOT NULL DEFAULT now(),
                    inst_category_id smallint,
                    inst_type_id smallint,
                    instruction text,
                    status boolean,
                    insert_user integer,
                    client_auth_user_id integer,
                    client_id bigint,
                    service_id bigint,
                    ip_address text,
                    inst_channel text,
                    attachment_id text,
                    instruction_id bigint,
                    remarks text,
                    expiry_date timestamptz,
                    completed boolean,
                    client_message_id uuid,
                    conversation_sequence bigint,
                    CONSTRAINT ck_instructions_conversation_sequence_shape CHECK (
                        client_message_id IS NULL
                        OR conversation_sequence > 0)
                );
                CREATE UNIQUE INDEX ix_instructions_conversation_sequence_unique
                    ON digital.instructions (instruction_id, conversation_sequence)
                    WHERE instruction_id IS NOT NULL;
                CREATE UNIQUE INDEX ix_instructions_client_message_unique
                    ON digital.instructions (client_message_id)
                    WHERE client_message_id IS NOT NULL;

                CREATE TABLE digital.conversation_access (
                    conversation_id bigint PRIMARY KEY REFERENCES digital.instructions(id),
                    client_id bigint NOT NULL,
                    conversation_kind varchar(16) NOT NULL,
                    state varchar(16) NOT NULL,
                    client_user_id integer,
                    admin_user_id integer,
                    version bigint NOT NULL DEFAULT 1,
                    created_at timestamptz NOT NULL DEFAULT now(),
                    archived_at timestamptz,
                    CONSTRAINT ck_conversation_access_kind
                        CHECK (conversation_kind IN ('Group','Private')),
                    CONSTRAINT ck_conversation_access_participants CHECK (
                        conversation_kind = 'Group'
                        OR conversation_kind = 'Private')
                );
                CREATE TABLE digital.conversation_sequences (
                    conversation_id bigint PRIMARY KEY REFERENCES digital.instructions(id),
                    next_sequence bigint NOT NULL
                );
                CREATE TABLE digital.conversation_read_cursors (
                    read_cursor_id bigint GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
                    conversation_id bigint NOT NULL REFERENCES digital.conversation_access(conversation_id),
                    principal_kind varchar(16) NOT NULL,
                    admin_user_id integer,
                    client_user_id integer,
                    last_read_sequence bigint NOT NULL DEFAULT 0,
                    updated_at timestamptz NOT NULL DEFAULT now()
                );
                CREATE TABLE digital.conversation_outbox (
                    event_id uuid PRIMARY KEY,
                    conversation_id bigint NOT NULL REFERENCES digital.conversation_access(conversation_id),
                    client_id bigint NOT NULL,
                    conversation_kind varchar(16) NOT NULL,
                    conversation_state varchar(16) NOT NULL,
                    client_user_id integer,
                    admin_user_id integer,
                    access_version bigint NOT NULL,
                    message_id bigint,
                    event_type varchar(64) NOT NULL,
                    schema_version smallint NOT NULL,
                    payload jsonb NOT NULL,
                    occurred_at timestamptz NOT NULL,
                    available_at timestamptz NOT NULL,
                    attempt_count integer NOT NULL,
                    lease_owner varchar(128),
                    lease_until timestamptz,
                    processed_at timestamptz,
                    dead_lettered_at timestamptz,
                    last_error_code varchar(64),
                    CONSTRAINT ck_conversation_outbox_kind
                        CHECK (conversation_kind IN ('Group','Private')),
                    CONSTRAINT ck_conversation_outbox_participants CHECK (
                        conversation_kind = 'Group'
                        OR conversation_kind = 'Private')
                );
                CREATE TABLE digital.conversation_audit (
                    audit_id bigint GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
                    conversation_id bigint NOT NULL REFERENCES digital.conversation_access(conversation_id),
                    client_id bigint NOT NULL,
                    action varchar(64) NOT NULL,
                    actor_kind varchar(16) NOT NULL,
                    admin_user_id integer,
                    client_user_id integer,
                    occurred_at timestamptz NOT NULL,
                    details jsonb NOT NULL
                );
                """);
        }

        public async Task ApplyCaseAndAttachmentMigrationsAsync()
        {
            await ApplyMigrationAsync(
                "202607261005_normalize_legacy_case_reply_shape.sql");
            await ApplyMigrationAsync("202607261010_modernize_case_conversations.sql");
            await ApplyMigrationAsync("202607261020_create_r2_attachments.sql");
            await ApplyMigrationAsync("202607261030_harden_r2_attachment_lifecycle.sql");
            await ApplyMigrationAsync(
                "202607261040_enforce_attachment_relational_invariants.sql");
        }

        public async Task SeedGroupConversationsAsync()
        {
            await ExecuteAsync("""
                INSERT INTO digital.instructions (
                    id, datetime, inst_category_id, inst_type_id, instruction,
                    status, client_auth_user_id, client_id, service_id,
                    inst_channel, instruction_id, conversation_sequence)
                VALUES
                    (1000, now(), 100, 100, NULL, TRUE, 7, 42, 3, 'chat', 1000, 0),
                    (1001, now(), 100, 100, NULL, TRUE, 7, 42, 3, 'chat', 1001, 0),
                    (1002, now(), 100, 100, 'Other message', TRUE, 7, 42, 3, 'chat', 1001, 1),
                    (2000, now(), 100, 100, NULL, TRUE, 8, 43, 3, 'chat', 2000, 0);
                INSERT INTO digital.conversation_access (
                    conversation_id, client_id, conversation_kind, state,
                    version, created_at)
                VALUES
                    (1000, 42, 'Group', 'Active', 1, now()),
                    (1001, 42, 'Group', 'Active', 1, now()),
                    (2000, 43, 'Group', 'Active', 1, now());
                INSERT INTO digital.conversation_sequences VALUES
                    (1000, 1), (1001, 2), (2000, 1);
                """);
        }

        public Task ApplyMigrationAsync(string fileName) =>
            ExecuteAsync(File.ReadAllText(Path.GetFullPath(Path.Combine(
                AppContext.BaseDirectory,
                "..",
                "..",
                "..",
                "..",
                "Database",
                "Migrations",
                fileName))));

        public async Task ExecuteAsync(string sql, object? parameters = null)
        {
            await using var connection = new NpgsqlConnection(ConnectionString);
            await connection.OpenAsync();
            await connection.ExecuteAsync(sql, parameters);
        }

        public async Task<T> QuerySingleAsync<T>(string sql, object? parameters = null)
        {
            await using var connection = new NpgsqlConnection(ConnectionString);
            return await connection.QuerySingleAsync<T>(sql, parameters);
        }

        public async Task<IEnumerable<T>> QueryAsync<T>(string sql, object? parameters = null)
        {
            await using var connection = new NpgsqlConnection(ConnectionString);
            return await connection.QueryAsync<T>(sql, parameters);
        }

        public async ValueTask DisposeAsync()
        {
            NpgsqlConnection.ClearAllPools();
            await using var connection = new NpgsqlConnection(_adminConnectionString);
            await connection.OpenAsync();
            await connection.ExecuteAsync(
                $"DROP DATABASE IF EXISTS \"{_databaseName}\" WITH (FORCE)");
        }
    }
}
