using Dapper;
using Npgsql;

namespace CBSSupport.API.Tests.Integration;

public sealed class LegacyPrivateMappingPostgreSqlIntegrationTests
{
    [PostgreSqlIntegrationFact]
    public async Task Migration_ExistingNeedsReviewMapping_PreservesUnresolvedReview()
    {
        await using var database = await TestDatabase.CreateAsync();
        await database.InitializeAsync();
        await database.SeedAccessAsync(233, 5, "Private", "NeedsReview", 109, 1, null);
        await database.ApplyMigrationAsync();

        Assert.Equal(1L, await database.QuerySingleAsync<long>(
            "SELECT count(*) FROM digital.private_conversation_review WHERE conversation_id = 233 AND review_state = 'NeedsReview';"));
        Assert.Equal("NeedsReview", await database.QuerySingleAsync<string>(
            "SELECT state FROM digital.conversation_access WHERE conversation_id = 233;"));
    }

    [PostgreSqlIntegrationFact]
    public async Task Migration_NeedsReviewMappingWithMissingReviewRow_RecreatesUnresolvedReview()
    {
        await using var database = await TestDatabase.CreateAsync();
        await database.InitializeAsync();
        await database.SeedEmptyReviewTableAsync();
        await database.SeedAccessAsync(233, 5, "Private", "NeedsReview", 109, 1, null);

        await database.ApplyMigrationAsync();

        Assert.Equal(1L, await database.QuerySingleAsync<long>(
            "SELECT count(*) FROM digital.private_conversation_review WHERE conversation_id = 233 AND review_state = 'NeedsReview';"));
    }

    [PostgreSqlIntegrationFact]
    public async Task Migration_ApprovedActiveAndArchivedMappings_ReconstructsResolvedReviewWithoutChangingState()
    {
        await using var database = await TestDatabase.CreateAsync();
        await database.InitializeAsync();
        await database.SeedApprovedAccessAsync(233, 5, "Active", 109, 1);
        await database.SeedApprovedAccessAsync(235, 5, "Archived", 109, 106);

        await database.ApplyMigrationAsync();

        var rows = await database.QueryAsync<(long ConversationId, string State, string ReviewState)>(
            """
            SELECT access.conversation_id AS ConversationId,
                   access.state AS State,
                   review.review_state AS ReviewState
            FROM digital.conversation_access access
            JOIN digital.private_conversation_review review
              ON review.conversation_id = access.conversation_id
            WHERE access.conversation_id IN (233, 235)
            ORDER BY access.conversation_id;
            """);

        Assert.Equal(
            [
                (233L, "Active", "Resolved"),
                (235L, "Archived", "Resolved")
            ],
            rows.ToArray());
    }

    [PostgreSqlIntegrationFact]
    public async Task Migration_ActiveWithoutApprovalEvidence_FailsAndRollsBack()
    {
        await using var database = await TestDatabase.CreateAsync();
        await database.InitializeAsync();
        await database.SeedAccessAsync(233, 5, "Private", "Active", 109, 1, null);

        var exception = await Assert.ThrowsAsync<PostgresException>(
            () => database.ApplyMigrationAsync());

        Assert.Contains("conversation_id=233", exception.Message, StringComparison.Ordinal);
        Assert.Contains("failed_predicate=missing_approval_evidence", exception.Message, StringComparison.Ordinal);
        Assert.False(await database.QuerySingleAsync<bool>(
            "SELECT to_regclass('digital.private_conversation_review') IS NOT NULL;"));
        Assert.Equal("Active", await database.QuerySingleAsync<string>(
            "SELECT state FROM digital.conversation_access WHERE conversation_id = 233;"));
    }

    [PostgreSqlIntegrationFact]
    public async Task Migration_ConflictingUnresolvedReviewForActiveMapping_FailsClosed()
    {
        await using var database = await TestDatabase.CreateAsync();
        await database.InitializeAsync();
        await database.SeedApprovedAccessAsync(233, 5, "Active", 109, 1);
        await database.SeedConflictingReviewAsync(233, 5, 1);

        Assert.Equal(1L, await database.QuerySingleAsync<long>(
            """
            SELECT count(*)
            FROM digital.conversation_audit
            WHERE conversation_id = 233
              AND client_id = 5
              AND action = 'LegacyPrivateApproved'
              AND actor_kind = 'Admin'
              AND admin_user_id = 1
              AND details->>'clientUserId' = '109'
              AND details->>'adminUserId' = '1'
              AND NULLIF(btrim(details->>'reason'), '') IS NOT NULL;
            """));

        var exception = await Assert.ThrowsAsync<PostgresException>(
            () => database.ApplyMigrationAsync());

        Assert.True(
            exception.MessageText.Contains(
                "failed_predicate=conflicting_review_state",
                StringComparison.Ordinal),
            $"""
            Expected predicate: conflicting_review_state

            Actual MessageText:
            {exception.MessageText}

            Detail:
            {exception.Detail}

            PostgreSQL Where:
            {exception.Where}
            """);
    }

    [PostgreSqlIntegrationFact]
    public async Task Migration_ArchivedWithoutApprovalEvidence_FailsClosed()
    {
        await using var database = await TestDatabase.CreateAsync();
        await database.InitializeAsync();
        await database.SeedAccessAsync(
            233, 5, "Private", "Archived", 109, 1, DateTimeOffset.UtcNow);

        var exception = await Assert.ThrowsAsync<PostgresException>(
            () => database.ApplyMigrationAsync());

        Assert.Contains("failed_predicate=missing_approval_evidence", exception.Message,
            StringComparison.Ordinal);
    }

    [PostgreSqlIntegrationFact]
    public async Task Migration_WithZeroLegacyRoots_InstallsReadinessObjectsWithoutFakeRows()
    {
        await using var database = await TestDatabase.CreateAsync();
        await database.InitializeAsync();

        await database.ApplyMigrationAsync();

        Assert.True(await database.QuerySingleAsync<bool>(
            "SELECT to_regclass('digital.private_conversation_review') IS NOT NULL;"));
        Assert.Equal(0L, await database.QuerySingleAsync<long>(
            "SELECT count(*) FROM digital.private_conversation_review;"));
        Assert.Equal(0L, await database.QuerySingleAsync<long>(
            "SELECT count(*) FROM digital.conversation_access;"));
    }

    [PostgreSqlIntegrationFact]
    public async Task Migration_RerunIsIdempotentAndDoesNotDuplicateEvidence()
    {
        await using var database = await TestDatabase.CreateAsync();
        await database.InitializeAsync();
        await database.SeedApprovedAccessAsync(233, 5, "Active", 109, 1);

        await database.ApplyMigrationAsync();
        await database.ApplyMigrationAsync();

        Assert.Equal(1L, await database.QuerySingleAsync<long>(
            "SELECT count(*) FROM digital.private_conversation_review WHERE conversation_id = 233;"));
        Assert.Equal(1L, await database.QuerySingleAsync<long>(
            "SELECT count(*) FROM digital.conversation_audit WHERE conversation_id = 233 AND action = 'LegacyPrivateReviewReconciled';"));
        Assert.Equal(1L, await database.QuerySingleAsync<long>(
            "SELECT count(*) FROM pg_trigger WHERE tgrelid = 'digital.conversation_access'::regclass AND tgname = 'trg_conversation_access_private_participant_tenant';"));
    }

    [PostgreSqlIntegrationFact]
    public async Task Migration_InvalidExistingShapes_FailClosedWithPredicateDiagnostics()
    {
        foreach (var scenario in Enum.GetValues<InvalidScenario>())
        {
            await using var database = await TestDatabase.CreateAsync();
            await database.InitializeAsync();
            await database.SeedScenarioAsync(scenario);

            var exception = await Assert.ThrowsAsync<PostgresException>(
                () => database.ApplyMigrationAsync());

            Assert.Contains($"failed_predicate={ToPredicate(scenario)}", exception.Message,
                StringComparison.Ordinal);
        }
    }

    private enum InvalidScenario
    {
        NonPrivate,
        TenantMismatch,
        InvalidClientParticipant,
        InvalidAdminParticipant,
        InvalidLifecycle,
        DuplicateActivePair
    }

    private static string ToPredicate(InvalidScenario scenario) => scenario switch
    {
        InvalidScenario.NonPrivate => "not_private_kind",
        InvalidScenario.TenantMismatch => "tenant_mismatch",
        InvalidScenario.InvalidClientParticipant => "invalid_client_participant",
        InvalidScenario.InvalidAdminParticipant => "invalid_admin_participant",
        InvalidScenario.InvalidLifecycle => "invalid_lifecycle_fields",
        InvalidScenario.DuplicateActivePair => "duplicate_active_pair",
        _ => throw new ArgumentOutOfRangeException(nameof(scenario))
    };

    private sealed class TestDatabase : IAsyncDisposable
    {
        private readonly string adminConnectionString;
        private readonly string databaseName;

        private TestDatabase(string adminConnectionString, string databaseName, string connectionString)
        {
            this.adminConnectionString = adminConnectionString;
            this.databaseName = databaseName;
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

            var databaseName = $"cbssupport_private_{Guid.NewGuid():N}";
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
            return new TestDatabase(admin.ConnectionString, databaseName, application.ConnectionString);
        }

        public async Task InitializeAsync()
        {
            await ExecuteAsync(
                """
                CREATE SCHEMA admin;
                CREATE SCHEMA internal;
                CREATE SCHEMA digital;

                CREATE TABLE admin.users (
                    id integer PRIMARY KEY,
                    status boolean NOT NULL DEFAULT TRUE,
                    deactive_date timestamptz
                );
                CREATE TABLE internal.support_users (
                    id integer PRIMARY KEY,
                    client_id integer NOT NULL,
                    status boolean NOT NULL DEFAULT TRUE,
                    deactive_date timestamptz
                );
                INSERT INTO admin.users (id) VALUES (1), (106);
                INSERT INTO internal.support_users (id, client_id) VALUES (109, 5), (110, 6);

                CREATE TABLE digital.instructions (
                    id bigint PRIMARY KEY,
                    client_id bigint,
                    instruction_id bigint,
                    inst_type_id smallint,
                    instruction text,
                    datetime timestamptz,
                    insert_date timestamptz NOT NULL DEFAULT now(),
                    client_message_id uuid,
                    conversation_sequence bigint
                );
                CREATE TABLE digital.conversation_access (
                    conversation_id bigint PRIMARY KEY
                        REFERENCES digital.instructions(id),
                    client_id bigint NOT NULL,
                    conversation_kind varchar(16) NOT NULL,
                    state varchar(16) NOT NULL,
                    client_user_id integer REFERENCES internal.support_users(id),
                    admin_user_id integer REFERENCES admin.users(id),
                    version bigint NOT NULL DEFAULT 1,
                    created_at timestamptz NOT NULL DEFAULT now(),
                    archived_at timestamptz
                );
                CREATE TABLE digital.conversation_audit (
                    audit_id bigint GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
                    conversation_id bigint NOT NULL
                        REFERENCES digital.conversation_access(conversation_id),
                    client_id bigint NOT NULL,
                    action varchar(64) NOT NULL,
                    actor_kind varchar(16) NOT NULL,
                    admin_user_id integer REFERENCES admin.users(id),
                    client_user_id integer REFERENCES internal.support_users(id),
                    occurred_at timestamptz NOT NULL DEFAULT now(),
                    details jsonb NOT NULL DEFAULT '{}'::jsonb
                );
                """);
        }

        public async Task SeedAccessAsync(
            long conversationId,
            long clientId,
            string kind,
            string state,
            int? clientUserId,
            int? adminUserId,
            DateTimeOffset? archivedAt)
        {
            await ExecuteAsync(
                """
                INSERT INTO digital.instructions (
                    id, client_id, instruction_id, inst_type_id, datetime)
                VALUES (@conversationId, @clientId, @conversationId, 101, now());

                INSERT INTO digital.conversation_access (
                    conversation_id, client_id, conversation_kind, state,
                    client_user_id, admin_user_id, archived_at)
                VALUES (
                    @conversationId, @clientId, @kind, @state,
                    @clientUserId, @adminUserId, @archivedAt);
                """,
                new
                {
                    conversationId,
                    clientId,
                    kind,
                    state,
                    clientUserId,
                    adminUserId,
                    archivedAt
                });
        }

        public async Task SeedApprovedAccessAsync(
            long conversationId,
            long clientId,
            string state,
            int clientUserId,
            int adminUserId)
        {
            await SeedAccessAsync(
                conversationId,
                clientId,
                "Private",
                state,
                clientUserId,
                adminUserId,
                state == "Archived" ? DateTimeOffset.UtcNow : null);

            await ExecuteAsync(
                """
                INSERT INTO digital.conversation_audit (
                    conversation_id, client_id, action, actor_kind,
                    admin_user_id, occurred_at, details)
                VALUES (
                    @conversationId, @clientId, 'LegacyPrivateApproved', 'Admin',
                    @adminUserId, now(),
                    jsonb_build_object(
                        'clientUserId', @clientUserId,
                        'adminUserId', @adminUserId,
                        'reason', 'reviewed'));
                """,
                new { conversationId, clientId, clientUserId, adminUserId });
        }

        public async Task SeedConflictingReviewAsync(
            long conversationId,
            long clientId,
            int resolvedByAdminUserId)
        {
            await SeedEmptyReviewTableAsync();
            await ExecuteAsync(
                """
                INSERT INTO digital.private_conversation_review (
                    conversation_id, client_id, review_state, remediation_code,
                    resolved_by_admin_user_id)
                VALUES (@conversationId, @clientId, 'NeedsReview',
                        'confirm_exact_client_and_admin_participants',
                        @resolvedByAdminUserId);
                """,
                new { conversationId, clientId, resolvedByAdminUserId });
        }

        public Task SeedEmptyReviewTableAsync() => ExecuteAsync(
            """
            CREATE TABLE digital.private_conversation_review (
                conversation_id bigint PRIMARY KEY,
                client_id bigint NOT NULL,
                review_state varchar(16) NOT NULL,
                remediation_code varchar(64) NOT NULL,
                created_at timestamptz NOT NULL DEFAULT now(),
                resolved_at timestamptz,
                resolved_by_admin_user_id integer
            );
            """);

        public async Task SeedScenarioAsync(InvalidScenario scenario)
        {
            switch (scenario)
            {
                case InvalidScenario.NonPrivate:
                    await SeedAccessAsync(233, 5, "Group", "Active", null, null, null);
                    break;
                case InvalidScenario.TenantMismatch:
                    await SeedAccessAsync(233, 5, "Private", "NeedsReview", 109, 1, null);
                    await ExecuteAsync(
                        "UPDATE digital.conversation_access SET client_id = 6 WHERE conversation_id = 233;");
                    break;
                case InvalidScenario.InvalidClientParticipant:
                    await SeedAccessAsync(233, 5, "Private", "NeedsReview", 110, 1, null);
                    break;
                case InvalidScenario.InvalidAdminParticipant:
                    await SeedAccessAsync(233, 5, "Private", "NeedsReview", 109, 106, null);
                    await ExecuteAsync(
                        "UPDATE admin.users SET status = FALSE WHERE id = 106;");
                    break;
                case InvalidScenario.InvalidLifecycle:
                    await SeedAccessAsync(233, 5, "Private", "Archived", 109, 1, null);
                    break;
                case InvalidScenario.DuplicateActivePair:
                    await SeedApprovedAccessAsync(233, 5, "Active", 109, 1);
                    await SeedApprovedAccessAsync(235, 5, "Active", 109, 1);
                    break;
            }
        }

        public async Task ApplyMigrationAsync()
        {
            var path = Path.GetFullPath(Path.Combine(
                AppContext.BaseDirectory, "..", "..", "..", "..",
                "Database", "Migrations",
                "202608051200_complete_legacy_private_mapping_gate.sql"));
            var sql = await File.ReadAllTextAsync(path);

            await using var connection = new NpgsqlConnection(ConnectionString);
            await connection.OpenAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            try
            {
                await connection.ExecuteAsync(sql, transaction: transaction);
                await transaction.CommitAsync();
            }
            catch
            {
                await transaction.RollbackAsync();
                throw;
            }
        }

        public async Task ExecuteAsync(string sql, object? parameters = null)
        {
            await using var connection = new NpgsqlConnection(ConnectionString);
            await connection.OpenAsync();
            await connection.ExecuteAsync(sql, parameters);
        }

        public async Task<T> QuerySingleAsync<T>(string sql)
        {
            await using var connection = new NpgsqlConnection(ConnectionString);
            await connection.OpenAsync();
            return await connection.QuerySingleAsync<T>(sql);
        }

        public async Task<IEnumerable<T>> QueryAsync<T>(string sql)
        {
            await using var connection = new NpgsqlConnection(ConnectionString);
            await connection.OpenAsync();
            return await connection.QueryAsync<T>(sql);
        }

        public async ValueTask DisposeAsync()
        {
            NpgsqlConnection.ClearAllPools();
            await using var connection = new NpgsqlConnection(adminConnectionString);
            await connection.OpenAsync();
            await connection.ExecuteAsync($"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE)");
        }
    }
}
