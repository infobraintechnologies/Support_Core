namespace CBSSupport.API.Tests.Integration;

public sealed class DatabaseMigrationContractTests
{
    [Fact]
    public void MigrationRunner_AlwaysPackagesCanonicalMigrationBytes()
    {
        var project = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "CBSSupport.DatabaseMigrator",
            "CBSSupport.DatabaseMigrator.csproj"));

        Assert.Contains(
            "CopyToOutputDirectory=\"Always\"",
            project,
            StringComparison.Ordinal);
        Assert.Contains(
            "CopyToPublishDirectory=\"Always\"",
            project,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "CopyToOutputDirectory=\"PreserveNewest\"",
            project,
            StringComparison.Ordinal);
    }

    [Fact]
    public void CaseReplyNormalization_RepairsOnlyReviewedLegacySentinelShape()
    {
        var sql = ReadMigration(
            "202607261005_normalize_legacy_case_reply_shape.sql");

        Assert.Contains(
            "LOCK TABLE digital.instructions IN SHARE ROW EXCLUSIVE MODE",
            sql,
            StringComparison.Ordinal);
        Assert.Contains(
            "-- migration-transaction: true",
            sql,
            StringComparison.Ordinal);
        Assert.Contains(
            "message.inst_type_id IN (100, root.inst_type_id)",
            sql,
            StringComparison.Ordinal);
        Assert.Contains(
            "message.inst_category_id IN (100, root.inst_category_id)",
            sql,
            StringComparison.Ordinal);
        Assert.Contains(
            "message.client_id IS NOT DISTINCT FROM root.client_id",
            sql,
            StringComparison.Ordinal);
        Assert.Contains(
            "SET inst_type_id = root.inst_type_id",
            sql,
            StringComparison.Ordinal);
        Assert.Contains(
            "inst_category_id = root.inst_category_id",
            sql,
            StringComparison.Ordinal);
        Assert.Contains(
            "ambiguous type/category mismatch",
            sql,
            StringComparison.Ordinal);
    }

    [Fact]
    public void CaseMigration_AssignsRootAndDeterministicReplySequences()
    {
        var sql = ReadMigration("202607261010_modernize_case_conversations.sql");

        Assert.Contains("SELECT root.id, 1", sql, StringComparison.Ordinal);
        Assert.Contains(
            "ORDER BY COALESCE(message.datetime, message.insert_date), message.id",
            sql,
            StringComparison.Ordinal);
        Assert.Contains("max(assignment.assigned_sequence) + 1", sql, StringComparison.Ordinal);
        Assert.Contains("ON CONFLICT (conversation_id) DO UPDATE", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void CaseMigration_RejectsCrossShapeChildrenAndCreatesMigrationAudit()
    {
        var sql = ReadMigration("202607261010_modernize_case_conversations.sql");

        Assert.Contains(
            "message.client_id IS DISTINCT FROM root.client_id",
            sql,
            StringComparison.Ordinal);
        Assert.Contains(
            "message.inst_type_id IS DISTINCT FROM root.inst_type_id",
            sql,
            StringComparison.Ordinal);
        Assert.Contains(
            "message.inst_category_id IS DISTINCT FROM root.inst_category_id",
            sql,
            StringComparison.Ordinal);
        Assert.Contains("'CaseHistorySequenced'", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void CaseAuditMigration_CreatesAppendOnlyTenantIndexedHistory()
    {
        var sql = ReadMigration("202608041100_create_case_audit.sql");

        Assert.Contains("CREATE TABLE digital.case_audit", sql, StringComparison.Ordinal);
        Assert.Contains("previous_version", sql, StringComparison.Ordinal);
        Assert.Contains("resulting_version", sql, StringComparison.Ordinal);
        Assert.Contains("actor_user_id", sql, StringComparison.Ordinal);
        Assert.Contains("changed_fields jsonb", sql, StringComparison.Ordinal);
        Assert.Contains("correlation_id", sql, StringComparison.Ordinal);
        Assert.Contains("ix_case_audit_case_occurred", sql, StringComparison.Ordinal);
        Assert.Contains("ix_case_audit_client_occurred", sql, StringComparison.Ordinal);
        Assert.Contains("trg_case_audit_append_only", sql, StringComparison.Ordinal);
        Assert.Contains("REVOKE UPDATE, DELETE, TRUNCATE", sql, StringComparison.Ordinal);
        Assert.Contains("GRANT INSERT ON TABLE digital.case_audit", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void CaseNotificationMigration_UsesExactRecipientsAndIdempotentOutboxKeys()
    {
        var sql = ReadMigration("202608051000_add_case_notification_delivery.sql");

        Assert.Contains("ADD COLUMN idempotency_key", sql, StringComparison.Ordinal);
        Assert.Contains("uq_conversation_outbox_idempotency_key", sql, StringComparison.Ordinal);
        Assert.Contains("CREATE TABLE digital.case_notifications", sql, StringComparison.Ordinal);
        Assert.Contains("recipient_kind", sql, StringComparison.Ordinal);
        Assert.Contains("case_version", sql, StringComparison.Ordinal);
        Assert.Contains("payload_version", sql, StringComparison.Ordinal);
        Assert.Contains("uq_case_notifications_idempotency_key", sql, StringComparison.Ordinal);
        Assert.Contains("enforce_case_notification_recipient", sql, StringComparison.Ordinal);
        Assert.Contains("recipient.client_id = NEW.client_id", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void AttachmentSchema_CountsDeletePendingUntilDeletionConfirmation()
    {
        var createSql = ReadMigration("202607261020_create_r2_attachments.sql");
        var invariantSql = ReadMigration(
            "202607261040_enforce_attachment_relational_invariants.sql");

        Assert.Contains(
            "'PendingUpload','Scanning','Promoting','Ready','DeletePending'",
            createSql,
            StringComparison.Ordinal);
        Assert.Contains(
            "state IN ('PendingUpload','Scanning','Promoting','Ready','DeletePending')",
            invariantSql,
            StringComparison.Ordinal);
        Assert.Contains(
            "reservation_bytes >= GREATEST",
            invariantSql,
            StringComparison.Ordinal);
        Assert.Contains(
            "maintain_attachment_quota_reservation",
            invariantSql,
            StringComparison.Ordinal);
    }

    [Fact]
    public void StructuralAttachmentMigration_AddsLifecycleStatesAndAuditsQueueReset()
    {
        var sql = ReadMigration("202608031000_structural_attachment_validation_mode.sql");

        Assert.Contains("'Uploaded','StructuralValidation','StructurallyValidated'", sql, StringComparison.Ordinal);
        Assert.Contains("'SecurityModeMigration'", sql, StringComparison.Ordinal);
        Assert.Contains("'securityMode', 'StructuralValidationOnly'", sql, StringComparison.Ordinal);
        Assert.Contains("'Scanning','Promoting','Ready','DeletePending'", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void AttachmentSchema_EnforcesTenantUploaderAndMessageBindingComposites()
    {
        var sql = ReadMigration("202607261040_enforce_attachment_relational_invariants.sql");

        Assert.Contains(
            "FOREIGN KEY (conversation_id, client_id)",
            sql,
            StringComparison.Ordinal);
        Assert.Contains(
            "FOREIGN KEY (message_id, conversation_id, client_id)",
            sql,
            StringComparison.Ordinal);
        Assert.Contains(
            "enforce_attachment_client_uploader_tenant",
            sql,
            StringComparison.Ordinal);
        Assert.Contains(
            "FOREIGN KEY (attachment_id, client_id)",
            sql,
            StringComparison.Ordinal);
    }

    [Fact]
    public void AttachmentSchema_EnforcesBoundRetentionFloor()
    {
        var sql = ReadMigration("202607261040_enforce_attachment_relational_invariants.sql");

        Assert.Contains(
            "expires_at >= bound_at + INTERVAL '365 days'",
            sql,
            StringComparison.Ordinal);
        Assert.Contains(
            "expires_at >= ready_at + INTERVAL '24 hours'",
            sql,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ClientPrincipalSchema_UsesIntegerSupportUserIdentityAndPreservesNullableInsertUser()
    {
        var principalSql = ReadMigration(
            "202607221000_canonical_instruction_client_principal.sql");
        var messagingSql = ReadMigration(
            "202607221110_create_messaging_v2_schema.sql");
        var attachmentSql = ReadMigration(
            "202607261020_create_r2_attachments.sql");

        Assert.Contains(
            "ALTER COLUMN client_auth_user_id TYPE integer",
            principalSql,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "ALTER COLUMN insert_user DROP NOT NULL",
            principalSql,
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(4, CountOccurrences(messagingSql, "client_user_id integer"));
        Assert.Equal(2, CountOccurrences(attachmentSql, "client_user_id integer"));
        Assert.DoesNotContain("client_user_id bigint", messagingSql, StringComparison.Ordinal);
        Assert.DoesNotContain("client_user_id bigint", attachmentSql, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "REFERENCES internal.support_users(user_id)",
            principalSql + messagingSql + attachmentSql,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("client_id bigint NOT NULL", messagingSql, StringComparison.Ordinal);
        Assert.Contains("client_id bigint PRIMARY KEY", attachmentSql, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "ALTER TABLE internal.clients",
            principalSql + messagingSql + attachmentSql,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ClientUserIdentityModels_UseNullableIntegerForOwnedMessagingColumns()
    {
        Assert.Equal(
            typeof(int?),
            typeof(CBSSupport.Shared.Contracts.ConversationAccess)
                .GetProperty("ClientUserId")!.PropertyType);
        Assert.Equal(
            typeof(int?),
            typeof(CBSSupport.Shared.Contracts.ConversationSummary)
                .GetProperty("ClientUserId")!.PropertyType);
        Assert.Equal(
            typeof(int?),
            typeof(CBSSupport.Shared.Contracts.ConversationOutboxItem)
                .GetProperty("ClientUserId")!.PropertyType);
        Assert.Equal(
            typeof(int?),
            typeof(CBSSupport.Shared.Services.AttachmentRecord)
                .GetProperty("ClientUserId")!.PropertyType);

        var conversationRepository = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "CBSSupport.Shared", "Services", "ConversationRepository.cs"));
        var outboxRepository = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "CBSSupport.Shared", "Services", "ConversationOutboxRepository.cs"));
        Assert.Contains("int? ClientUserId", conversationRepository, StringComparison.Ordinal);
        Assert.Contains("int? ClientUserId", outboxRepository, StringComparison.Ordinal);
    }

    [Fact]
    public void PrincipalPreflight_VerifiesConfirmedClientKeyWithoutChangingExternalSchema()
    {
        var sql = ReadPreflight("202607211010_verify_instruction_principals.sql");

        Assert.Contains(
            "format_type(column_row.atttypid, column_row.atttypmod) = 'integer'",
            sql,
            StringComparison.Ordinal);
        Assert.Contains("column_row.attnotnull", sql, StringComparison.Ordinal);
        Assert.Contains("key_constraint.contype IN ('p', 'u')", sql, StringComparison.Ordinal);
        Assert.Contains(
            "'digital.instructions.client_id must remain bigint'",
            sql,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "ALTER TABLE internal.clients",
            sql,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ManualDeployment_RequiresEmptyTypeCorrectionAndNeverChangesInsertUserNullability()
    {
        var sql = ReadManualDeployment(
            "20260803_messaging_attachments_test.sql");

        Assert.Contains(
            "ALTER COLUMN client_auth_user_id TYPE integer",
            sql,
            StringComparison.Ordinal);
        Assert.Contains(
            "WHERE client_auth_user_id IS NOT NULL",
            sql,
            StringComparison.Ordinal);
        Assert.Contains(
            "('internal','support_users','id','integer')",
            sql,
            StringComparison.Ordinal);
        Assert.Contains(
            "('internal','support_users','client_id','integer')",
            sql,
            StringComparison.Ordinal);
        Assert.Contains(
            "('internal','clients','id','integer')",
            sql,
            StringComparison.Ordinal);
        Assert.Contains(
            "internal.clients.id must be NOT NULL and a single-column primary or unique key",
            sql,
            StringComparison.Ordinal);
        Assert.DoesNotContain("client_user_id bigint", sql, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "ALTER COLUMN insert_user DROP NOT NULL",
            sql,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "REFERENCES internal.support_users(user_id)",
            sql,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "ALTER TABLE internal.clients",
            sql,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ManualDeployment_ExistingMessagingSchemaWithEmptyBigintAndRemovedFk_ReconcilesIdentityBeforeValidation()
    {
        var sql = ReadManualDeployment(
            "20260803_messaging_attachments_test.sql");
        var conversionBlock = ReadDollarQuotedBlock(sql, "instruction_principal_type");
        var identityFkBlock = ReadDollarQuotedBlock(sql, "instruction_client_identity_fk");
        var finalValidationBlock = ReadDollarQuotedBlock(sql, "post_deployment_validation");

        Assert.DoesNotContain("deploy_required", conversionBlock, StringComparison.Ordinal);
        Assert.DoesNotContain("deploy_required", identityFkBlock, StringComparison.Ordinal);
        Assert.Contains("IF current_type = 'bigint' THEN", conversionBlock, StringComparison.Ordinal);
        Assert.Contains(
            "ELSIF current_type IS DISTINCT FROM 'integer' THEN",
            conversionBlock,
            StringComparison.Ordinal);
        Assert.Contains(
            "WHERE client_auth_user_id IS NOT NULL",
            conversionBlock,
            StringComparison.Ordinal);
        Assert.Contains(
            "Cannot convert bigint client_auth_user_id while a blocking foreign key remains",
            conversionBlock,
            StringComparison.Ordinal);
        Assert.Contains(
            "ALTER COLUMN client_auth_user_id TYPE integer",
            conversionBlock,
            StringComparison.Ordinal);
        Assert.Contains(
            "WHEN client_auth_user_id IS NULL THEN NULL::integer",
            conversionBlock,
            StringComparison.Ordinal);
        Assert.Contains(
            "ADD CONSTRAINT fk_instructions_client_auth_support_user",
            identityFkBlock,
            StringComparison.Ordinal);
        Assert.Contains(
            "FOREIGN KEY (client_auth_user_id)",
            identityFkBlock,
            StringComparison.Ordinal);
        Assert.Contains(
            "REFERENCES internal.support_users(id)",
            identityFkBlock,
            StringComparison.Ordinal);
        Assert.Contains(
            "VALIDATE CONSTRAINT fk_instructions_client_auth_support_user",
            identityFkBlock,
            StringComparison.Ordinal);
        Assert.DoesNotContain("NOT VALID", identityFkBlock, StringComparison.Ordinal);
        Assert.Contains(
            "Incompatible client_auth_user_id foreign key exists",
            identityFkBlock,
            StringComparison.Ordinal);
        Assert.Contains("constraint_row.convalidated", identityFkBlock, StringComparison.Ordinal);
        Assert.Contains(
            "constraint_row.confrelid = 'internal.support_users'::regclass",
            identityFkBlock,
            StringComparison.Ordinal);
        Assert.Contains(
            "constraint_row.conname = 'fk_instructions_client_auth_support_user'",
            finalValidationBlock,
            StringComparison.Ordinal);
        Assert.Contains("constraint_row.convalidated", finalValidationBlock, StringComparison.Ordinal);
        Assert.Contains(
            "column_row.attname = 'client_auth_user_id'",
            finalValidationBlock,
            StringComparison.Ordinal);
        Assert.Contains(
            "constraint_row.confrelid = 'internal.support_users'::regclass",
            finalValidationBlock,
            StringComparison.Ordinal);

        var conversionIndex = sql.IndexOf(
            "DO $instruction_principal_type$",
            StringComparison.Ordinal);
        var identityFkIndex = sql.IndexOf(
            "DO $instruction_client_identity_fk$",
            StringComparison.Ordinal);
        var newTableBranchIndex = sql.IndexOf(
            "DO $instruction_columns$",
            StringComparison.Ordinal);
        var finalValidationIndex = sql.IndexOf(
            "DO $post_deployment_validation$",
            StringComparison.Ordinal);

        Assert.True(conversionIndex >= 0 && conversionIndex < identityFkIndex);
        Assert.True(identityFkIndex < newTableBranchIndex);
        Assert.True(identityFkIndex < finalValidationIndex);
    }

    [Fact]
    public void ManualDeployment_FreshCreateDefinesAllSixClientUserColumnsAsInteger()
    {
        var sql = ReadManualDeployment("20260803_messaging_attachments_test.sql");
        var createBlock = ReadDollarQuotedBlock(sql, "create_tables");

        Assert.Equal(6, CountOccurrences(createBlock, "client_user_id integer"));
        Assert.DoesNotContain("client_user_id bigint", createBlock, StringComparison.Ordinal);
        foreach (var constraintName in ClientUserForeignKeys)
        {
            Assert.Contains($"CONSTRAINT {constraintName}", createBlock, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ManualDeployment_AttachmentTenantQuotaPrimaryKey_LeavesTheExactValidatedNameIntact()
    {
        var repairBlock = ReadDollarQuotedBlock(
            ReadManualDeployment("20260803_messaging_attachments_test.sql"),
            "attachment_tenant_quota_primary_key");

        Assert.Contains("constraint_row.conname = 'pk_attachment_tenant_quotas'", repairBlock, StringComparison.Ordinal);
        Assert.Contains("constraint_row.contype = 'p'", repairBlock, StringComparison.Ordinal);
        Assert.Contains("named_constraint.convalidated", repairBlock, StringComparison.Ordinal);
        Assert.Contains("named_constraint.conkey <> ARRAY[client_id_attnum]::smallint[]", repairBlock, StringComparison.Ordinal);
        Assert.Contains("named_constraint.indisprimary", repairBlock, StringComparison.Ordinal);
        Assert.Contains("named_constraint.indisunique", repairBlock, StringComparison.Ordinal);
        Assert.Contains("named_constraint.indisvalid", repairBlock, StringComparison.Ordinal);
        Assert.Contains("named_constraint.indisready", repairBlock, StringComparison.Ordinal);
        Assert.Contains("named_constraint.indislive", repairBlock, StringComparison.Ordinal);
    }

    [Fact]
    public void ManualDeployment_AttachmentTenantQuotaPrimaryKey_RenamesEquivalentDefaultNamedPrimaryKey()
    {
        var repairBlock = ReadDollarQuotedBlock(
            ReadManualDeployment("20260803_messaging_attachments_test.sql"),
            "attachment_tenant_quota_primary_key");

        Assert.Contains(
            "ALTER TABLE digital.attachment_tenant_quotas RENAME CONSTRAINT %I TO pk_attachment_tenant_quotas",
            repairBlock,
            StringComparison.Ordinal);
        Assert.Contains("primary_key_count = 1", repairBlock, StringComparison.Ordinal);
    }

    [Fact]
    public void ManualDeployment_AttachmentTenantQuotaPrimaryKey_RejectsConflictingExpectedName()
    {
        var repairBlock = ReadDollarQuotedBlock(
            ReadManualDeployment("20260803_messaging_attachments_test.sql"),
            "attachment_tenant_quota_primary_key");

        Assert.Contains("Conflicting constraint named pk_attachment_tenant_quotas", repairBlock, StringComparison.Ordinal);
        Assert.Contains("constraint_row.conname = 'pk_attachment_tenant_quotas'", repairBlock, StringComparison.Ordinal);
        Assert.Contains("named_constraint.contype <> 'p'", repairBlock, StringComparison.Ordinal);
    }

    [Fact]
    public void ManualDeployment_AttachmentTenantQuotaPrimaryKey_RejectsPrimaryKeyOnWrongColumns()
    {
        var repairBlock = ReadDollarQuotedBlock(
            ReadManualDeployment("20260803_messaging_attachments_test.sql"),
            "attachment_tenant_quota_primary_key");

        Assert.Contains("primary key % is on different columns; expected (client_id)", repairBlock, StringComparison.Ordinal);
        Assert.Contains("alternative_primary_key.conkey <> ARRAY[client_id_attnum]::smallint[]", repairBlock, StringComparison.Ordinal);
    }

    [Fact]
    public void ManualDeployment_AttachmentTenantQuotaPrimaryKey_AddsMissingPrimaryKeyAfterDataPreflight()
    {
        var repairBlock = ReadDollarQuotedBlock(
            ReadManualDeployment("20260803_messaging_attachments_test.sql"),
            "attachment_tenant_quota_primary_key");

        Assert.Contains("WHERE client_id IS NULL", repairBlock, StringComparison.Ordinal);
        Assert.Contains("GROUP BY client_id", repairBlock, StringComparison.Ordinal);
        Assert.Contains("HAVING count(*) > 1", repairBlock, StringComparison.Ordinal);
        Assert.Contains(
            "ADD CONSTRAINT pk_attachment_tenant_quotas PRIMARY KEY (client_id)",
            repairBlock,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ManualDeployment_AttachmentTenantQuotaPrimaryKey_RejectsInvalidSupportingIndex()
    {
        var repairBlock = ReadDollarQuotedBlock(
            ReadManualDeployment("20260803_messaging_attachments_test.sql"),
            "attachment_tenant_quota_primary_key");

        Assert.Contains("supporting index", repairBlock, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("NOT alternative_primary_key.indisprimary", repairBlock, StringComparison.Ordinal);
        Assert.Contains("NOT alternative_primary_key.indisunique", repairBlock, StringComparison.Ordinal);
        Assert.Contains("NOT alternative_primary_key.indisvalid", repairBlock, StringComparison.Ordinal);
        Assert.Contains("NOT alternative_primary_key.indisready", repairBlock, StringComparison.Ordinal);
        Assert.Contains("NOT alternative_primary_key.indislive", repairBlock, StringComparison.Ordinal);
    }

    [Fact]
    public void ManualDeploymentAndStandaloneVerification_RequireTheCanonicalAttachmentTenantQuotaPrimaryKeyName()
    {
        var deploymentValidationBlock = ReadDollarQuotedBlock(
            ReadManualDeployment("20260803_messaging_attachments_test.sql"),
            "post_deployment_validation");
        var verificationSql = ReadManualDeployment(
            "20260803_verify_messaging_attachments_test.sql");

        Assert.Contains("pk_attachment_tenant_quotas", deploymentValidationBlock, StringComparison.Ordinal);
        Assert.Contains("constraint_row.convalidated", deploymentValidationBlock, StringComparison.Ordinal);
        Assert.Contains(
            "('attachment_tenant_quotas','pk_attachment_tenant_quotas','p','client_id',NULL,NULL)",
            verificationSql,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ManualDeployment_ExistingEmptyBigintClientUserColumns_AreCorrectedOutsideFreshCreateBranch()
    {
        var sql = ReadManualDeployment("20260803_messaging_attachments_test.sql");
        var correctionBlock = ReadDollarQuotedBlock(sql, "client_user_identity_columns");

        Assert.DoesNotContain("(SELECT deploy_required", correctionBlock, StringComparison.Ordinal);
        Assert.Contains("current_type = 'bigint'", correctionBlock, StringComparison.Ordinal);
        Assert.Contains(
            "ALTER COLUMN client_user_id TYPE integer USING (CASE WHEN client_user_id IS NULL THEN NULL::integer ELSE client_user_id::integer END)",
            correctionBlock,
            StringComparison.Ordinal);
        Assert.Contains("DROP CONSTRAINT %I", correctionBlock, StringComparison.Ordinal);
        Assert.Contains("REFERENCES internal.support_users(id) NOT VALID", correctionBlock, StringComparison.Ordinal);
        Assert.Contains("VALIDATE CONSTRAINT %I", correctionBlock, StringComparison.Ordinal);
    }

    [Fact]
    public void ManualDeployment_AttachmentBigintRerun_RepairsOnlyTheExpectedUploaderTenantTrigger()
    {
        var sql = ReadManualDeployment("20260803_messaging_attachments_test.sql");
        var correctionBlock = ReadDollarQuotedBlock(sql, "client_user_identity_columns");
        var freshTriggerBlock = ReadDollarQuotedBlock(sql, "create_functions_and_triggers");

        const string uploaderFunction = "digital.enforce_attachment_client_uploader_tenant()";
        const string canonicalCreateTrigger = "CREATE TRIGGER trg_attachments_client_uploader_tenant";

        Assert.Contains("current_type = 'bigint'", correctionBlock, StringComparison.Ordinal);
        Assert.Contains("'digital.attachments'::regclass", correctionBlock, StringComparison.Ordinal);
        Assert.Contains("DROP TRIGGER trg_attachments_client_uploader_tenant ON digital.attachments", correctionBlock, StringComparison.Ordinal);
        Assert.Contains(canonicalCreateTrigger, correctionBlock, StringComparison.Ordinal);
        Assert.Contains("BEFORE INSERT OR UPDATE OF client_user_id, client_id", correctionBlock, StringComparison.Ordinal);
        Assert.Contains("ON digital.attachments", correctionBlock, StringComparison.Ordinal);
        Assert.Contains("FOR EACH ROW", correctionBlock, StringComparison.Ordinal);
        Assert.Contains("EXECUTE FUNCTION digital.enforce_attachment_client_uploader_tenant();", correctionBlock, StringComparison.Ordinal);
        Assert.Contains(canonicalCreateTrigger, freshTriggerBlock, StringComparison.Ordinal);
        Assert.Contains("BEFORE INSERT OR UPDATE OF client_user_id, client_id", freshTriggerBlock, StringComparison.Ordinal);
        Assert.Contains("EXECUTE FUNCTION digital.enforce_attachment_client_uploader_tenant();", freshTriggerBlock, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(freshTriggerBlock, canonicalCreateTrigger));
        Assert.DoesNotContain("DROP FUNCTION digital.enforce_attachment_client_uploader_tenant", correctionBlock, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DROP TRIGGER trg_attachments_quota_reservation", correctionBlock, StringComparison.OrdinalIgnoreCase);

        var preflightIndex = correctionBlock.IndexOf(uploaderFunction, StringComparison.Ordinal);
        var dropIndex = correctionBlock.IndexOf(
            "DROP TRIGGER trg_attachments_client_uploader_tenant ON digital.attachments",
            StringComparison.Ordinal);
        var alterIndex = correctionBlock.IndexOf(
            "ALTER COLUMN client_user_id TYPE integer USING (CASE WHEN client_user_id IS NULL THEN NULL::integer ELSE client_user_id::integer END)",
            StringComparison.Ordinal);
        var recreateIndex = correctionBlock.IndexOf(canonicalCreateTrigger, StringComparison.Ordinal);

        Assert.True(preflightIndex >= 0 && preflightIndex < dropIndex,
            "The uploader trigger function must be verified before its trigger is dropped.");
        Assert.True(dropIndex < alterIndex,
            "The dependent uploader trigger must be dropped before attachments.client_user_id is narrowed.");
        Assert.True(alterIndex < recreateIndex,
            "The uploader trigger must be recreated after attachments.client_user_id is narrowed.");
    }

    [Fact]
    public void ManualDeployment_AttachmentTriggerRepair_RejectsUnexpectedDefinitionsAndDependencies()
    {
        var correctionBlock = ReadDollarQuotedBlock(
            ReadManualDeployment("20260803_messaging_attachments_test.sql"),
            "client_user_identity_columns");

        Assert.Contains("trigger_row.tgrelid = 'digital.attachments'::regclass", correctionBlock, StringComparison.Ordinal);
        Assert.Contains(
            "attachment_trigger_function_oid :=\n        to_regprocedure('digital.enforce_attachment_client_uploader_tenant()');",
            correctionBlock,
            StringComparison.Ordinal);
        Assert.Contains("trigger_row.tgfoid = attachment_trigger_function_oid", correctionBlock, StringComparison.Ordinal);
        Assert.Contains("trigger_row.tgtype = 23", correctionBlock, StringComparison.Ordinal);
        Assert.Contains("trigger_row.tgenabled = 'O'", correctionBlock, StringComparison.Ordinal);
        Assert.Contains("trigger_row.tgattr::text", correctionBlock, StringComparison.Ordinal);
        Assert.Contains("Unexpected dependent trigger", correctionBlock, StringComparison.Ordinal);
        Assert.Contains("trg_attachments_client_uploader_tenant", correctionBlock, StringComparison.Ordinal);
    }

    [Fact]
    public void ManualDeployment_ExistingValidPopulatedBigintClientUserColumns_AreValidatedBeforeDdl()
    {
        var sql = ReadManualDeployment("20260803_messaging_attachments_test.sql");
        var correctionBlock = ReadDollarQuotedBlock(sql, "client_user_identity_columns");

        foreach (var tableName in ClientUserTables)
        {
            Assert.Contains($"'digital.{tableName}'::regclass", correctionBlock, StringComparison.Ordinal);
        }
        Assert.Contains(
            "support_user.id::bigint = candidate.client_user_id",
            correctionBlock,
            StringComparison.Ordinal);
        Assert.True(
            correctionBlock.IndexOf("WITH candidates", StringComparison.Ordinal)
            < correctionBlock.IndexOf("DROP CONSTRAINT %I", StringComparison.Ordinal));
    }

    [Fact]
    public void ManualDeployment_OutOfRangeClientUserId_AbortsBeforeNarrowing()
    {
        var correctionBlock = ReadDollarQuotedBlock(
            ReadManualDeployment("20260803_messaging_attachments_test.sql"),
            "client_user_identity_columns");

        Assert.Contains(
            "candidate.client_user_id NOT BETWEEN -2147483648 AND 2147483647",
            correctionBlock,
            StringComparison.Ordinal);
        Assert.Contains(
            "is outside PostgreSQL integer range",
            correctionBlock,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ManualDeployment_MissingSupportUser_AbortsBeforeNarrowing()
    {
        var correctionBlock = ReadDollarQuotedBlock(
            ReadManualDeployment("20260803_messaging_attachments_test.sql"),
            "client_user_identity_columns");

        Assert.Contains("support_user.id IS NULL", correctionBlock, StringComparison.Ordinal);
        Assert.Contains(
            "does not reference internal.support_users(id)",
            correctionBlock,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ManualDeployment_CrossTenantSupportUser_AbortsBeforeNarrowing()
    {
        var correctionBlock = ReadDollarQuotedBlock(
            ReadManualDeployment("20260803_messaging_attachments_test.sql"),
            "client_user_identity_columns");

        Assert.Contains(
            "support_user.client_id::bigint IS DISTINCT FROM candidate.client_id",
            correctionBlock,
            StringComparison.Ordinal);
        Assert.Contains("belongs to client %s, expected client %s", correctionBlock, StringComparison.Ordinal);
    }

    [Fact]
    public void ManualDeployment_PostValidationRequiresAllSixClientUserColumnsToBeInteger()
    {
        var validationBlock = ReadDollarQuotedBlock(
            ReadManualDeployment("20260803_messaging_attachments_test.sql"),
            "post_deployment_validation");

        foreach (var tableName in ClientUserTables)
        {
            Assert.Contains($"('{tableName}')", validationBlock, StringComparison.Ordinal);
        }
        Assert.Contains(
            "format_type(column_row.atttypid, column_row.atttypmod) <> 'integer'",
            validationBlock,
            StringComparison.Ordinal);
        Assert.Contains(
            "every Messaging V2 and attachment client_user_id column must be integer",
            validationBlock,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ManualDeployment_PostValidationRequiresExactEnabledAttachmentUploaderTrigger()
    {
        var validationBlock = ReadDollarQuotedBlock(
            ReadManualDeployment("20260803_messaging_attachments_test.sql"),
            "post_deployment_validation");

        Assert.Contains("trigger_row.tgrelid = 'digital.attachments'::regclass", validationBlock, StringComparison.Ordinal);
        Assert.Contains("trigger_row.tgname = 'trg_attachments_client_uploader_tenant'", validationBlock, StringComparison.Ordinal);
        Assert.Contains(
            "trigger_row.tgfoid = 'digital.enforce_attachment_client_uploader_tenant()'::regprocedure",
            validationBlock,
            StringComparison.Ordinal);
        Assert.Contains("trigger_row.tgtype = 23", validationBlock, StringComparison.Ordinal);
        Assert.Contains("trigger_row.tgattr::text", validationBlock, StringComparison.Ordinal);
        Assert.Contains("trigger_row.tgenabled = 'O'", validationBlock, StringComparison.Ordinal);
        Assert.Contains("NOT trigger_row.tgisinternal", validationBlock, StringComparison.Ordinal);
        Assert.Contains("trg_attachments_client_uploader_tenant definition is missing, disabled, or incompatible", validationBlock, StringComparison.Ordinal);
    }

    [Fact]
    public void ManualVerification_RequiresIntegerClientPrincipalsAndTenantMatches()
    {
        var sql = ReadManualDeployment(
            "20260803_verify_messaging_attachments_test.sql");

        Assert.Contains(
            "('instructions','client_auth_user_id','integer',false,'')",
            sql,
            StringComparison.Ordinal);
        Assert.Contains(
            "('conversation_access','client_user_id','integer',false,'')",
            sql,
            StringComparison.Ordinal);
        Assert.Contains(
            "('attachments','client_user_id','integer',false,'')",
            sql,
            StringComparison.Ordinal);
        foreach (var tableName in ClientUserTables)
        {
            Assert.Contains(
                $"('{tableName}','client_user_id','integer',false,'')",
                sql,
                StringComparison.Ordinal);
        }
        Assert.Contains(
            "('internal','clients','id','integer')",
            sql,
            StringComparison.Ordinal);
        Assert.Contains("column_row.attnotnull", sql, StringComparison.Ordinal);
        Assert.Contains(
            "('conversation_access','client_id','bigint',true,'')",
            sql,
            StringComparison.Ordinal);
        Assert.Contains("DO $instruction_client_identity_fk$", sql, StringComparison.Ordinal);
        Assert.Contains(
            "constraint_row.conname = 'fk_instructions_client_auth_support_user'",
            sql,
            StringComparison.Ordinal);
        Assert.Contains("constraint_row.convalidated", sql, StringComparison.Ordinal);
        Assert.Contains(
            "constraint_row.confrelid = 'internal.support_users'::regclass",
            sql,
            StringComparison.Ordinal);
        Assert.Contains(
            "column_row.attname = 'client_auth_user_id'",
            sql,
            StringComparison.Ordinal);
        Assert.DoesNotContain("'client_user_id','bigint'", sql, StringComparison.Ordinal);
        Assert.Contains(
            "support_user.client_id IS DISTINCT FROM access.client_id",
            sql,
            StringComparison.Ordinal);
        Assert.Contains(
            "support_user.client_id IS DISTINCT FROM instruction.client_id",
            sql,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "REFERENCES internal.support_users(user_id)",
            sql,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ManualVerification_RemainsStrictForAttachmentUploaderTenantTrigger()
    {
        var verificationSql = ReadManualDeployment(
            "20260803_verify_messaging_attachments_test.sql");

        Assert.Contains(
            "trigger_row.tgname = 'trg_attachments_client_uploader_tenant'",
            verificationSql,
            StringComparison.Ordinal);
        Assert.Contains(
            "trigger_row.tgfoid = 'digital.enforce_attachment_client_uploader_tenant()'::regprocedure",
            verificationSql,
            StringComparison.Ordinal);
        Assert.Contains("trigger_row.tgtype = 23", verificationSql, StringComparison.Ordinal);
        Assert.Contains("trigger_row.tgattr::text = array_to_string(expected_update_columns, ' ')", verificationSql, StringComparison.Ordinal);
        Assert.Contains("trigger_row.tgenabled = 'O'", verificationSql, StringComparison.Ordinal);
        Assert.Contains("NOT trigger_row.tgisinternal", verificationSql, StringComparison.Ordinal);
    }

 private static string ReadMigration(string fileName) =>
    ReadSqlFile(Path.Combine(
        FindRepositoryRoot(),
        "Database",
        "Migrations",
        fileName));

private static string ReadManualDeployment(string fileName) =>
    ReadSqlFile(Path.Combine(
        FindRepositoryRoot(),
        "Database",
        "ManualDeployments",
        fileName));

private static string ReadPreflight(string fileName) =>
    ReadSqlFile(Path.Combine(
        FindRepositoryRoot(),
        "Database",
        "Preflight",
        fileName));

private static string ReadSqlFile(string path)
{
    return File.ReadAllText(path)
        .Replace("\r\n", "\n", StringComparison.Ordinal)
        .Replace('\r', '\n');
}

    private static string ReadDollarQuotedBlock(string sql, string blockName)
    {
        var marker = $"${blockName}$";
        var start = sql.IndexOf($"DO {marker}", StringComparison.Ordinal);
        Assert.True(start >= 0, $"Missing SQL block {blockName}.");
        var end = sql.IndexOf($"{marker};", start, StringComparison.Ordinal);
        Assert.True(end >= 0, $"Unterminated SQL block {blockName}.");
        return sql[start..(end + marker.Length + 1)];
    }

    private static int CountOccurrences(string value, string search)
    {
        var count = 0;
        var offset = 0;
        while ((offset = value.IndexOf(search, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += search.Length;
        }
        return count;
    }

    private static readonly string[] ClientUserTables =
    [
        "conversation_access",
        "conversation_read_cursors",
        "conversation_outbox",
        "conversation_audit",
        "attachments",
        "attachment_audit"
    ];

    private static readonly string[] ClientUserForeignKeys =
    [
        "fk_conversation_access_client_user",
        "fk_conversation_read_cursors_client_user",
        "fk_conversation_outbox_client_user",
        "fk_conversation_audit_client_user",
        "fk_attachments_client_user",
        "fk_attachment_audit_client_user"
    ];

    private static string FindRepositoryRoot()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            var directory = new DirectoryInfo(start);
            while (directory is not null)
            {
                if (Directory.Exists(Path.Combine(directory.FullName, "Database"))
                    && File.Exists(Path.Combine(directory.FullName, "CBSSupportSolution.sln")))
                {
                    return directory.FullName;
                }
                directory = directory.Parent;
            }
        }
        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }
}
