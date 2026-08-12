-- Record content-free review state for legacy Private roots and enforce the gate.
-- Preconditions: Messaging V2 schema and history backfill are applied.
-- migration-transaction: true
-- Retain review/audit evidence. Use a reviewed forward-fix; never resequence history.

DO $legacy_private_mapping_guard$
BEGIN
    -- Type-101 records must have same-tenant canonical roots.
    IF EXISTS (
        SELECT 1
        FROM digital.instructions AS message
        LEFT JOIN digital.instructions AS root
          ON root.id = message.instruction_id
        WHERE message.inst_type_id = 101
          AND (root.id IS NULL
               OR root.instruction_id IS DISTINCT FROM root.id
               OR message.client_id IS DISTINCT FROM root.client_id
               OR root.client_id IS NULL)
    ) THEN
        RAISE EXCEPTION 'Legacy Private mapping found a noncanonical, tenantless, or cross-tenant type-101 record';
    END IF;

    -- Accept only exact durable approval evidence, not lifecycle state alone.
    DECLARE
        conflict_conversation_id bigint;
        conflict_client_id bigint;
        conflict_kind varchar(16);
        conflict_state varchar(16);
        failed_predicate text;
        review_table_exists boolean := to_regclass('digital.private_conversation_review') IS NOT NULL;
    BEGIN
        IF review_table_exists THEN
            EXECUTE $private_mapping_guard_query$
                SELECT evaluated.conversation_id,
                       evaluated.client_id,
                       evaluated.conversation_kind,
                       evaluated.state,
                       evaluated.failed_predicate
                FROM (
                    SELECT root.id AS conversation_id,
                           root.client_id,
                           access.conversation_kind,
                           access.state,
                           CASE
                               WHEN access.conversation_kind IS DISTINCT FROM 'Private'
                                   THEN 'not_private_kind'
                               WHEN access.client_id IS DISTINCT FROM root.client_id
                                   THEN 'tenant_mismatch'
                               WHEN access.state NOT IN ('NeedsReview', 'Active', 'Archived')
                                   THEN 'invalid_lifecycle_fields'
                               WHEN access.client_user_id IS NOT NULL
                                    AND NOT EXISTS (
                                        SELECT 1
                                        FROM internal.support_users AS client_user
                                        WHERE client_user.id = access.client_user_id
                                          AND client_user.client_id::bigint = access.client_id
                                          AND client_user.status IS TRUE
                                          AND client_user.deactive_date IS NULL)
                                   THEN 'invalid_client_participant'
                               WHEN access.admin_user_id IS NOT NULL
                                    AND NOT EXISTS (
                                        SELECT 1
                                        FROM admin.users AS admin_user
                                        WHERE admin_user.id = access.admin_user_id
                                          AND admin_user.status IS TRUE
                                          AND admin_user.deactive_date IS NULL)
                                   THEN 'invalid_admin_participant'
                               WHEN access.state = 'NeedsReview'
                                    AND access.archived_at IS NOT NULL
                                   THEN 'invalid_lifecycle_fields'
                               WHEN access.state = 'Active'
                                    AND access.archived_at IS NOT NULL
                                   THEN 'invalid_lifecycle_fields'
                               WHEN access.state = 'Archived'
                                    AND access.archived_at IS NULL
                                   THEN 'invalid_lifecycle_fields'
                               WHEN access.state IN ('Active', 'Archived')
                                    AND (access.client_user_id IS NULL OR access.admin_user_id IS NULL)
                                   THEN 'invalid_lifecycle_fields'
                               WHEN access.state = 'Active'
                                    AND EXISTS (
                                        SELECT 1
                                        FROM digital.conversation_access AS duplicate_access
                                        WHERE duplicate_access.conversation_kind = 'Private'
                                          AND duplicate_access.state = 'Active'
                                          AND duplicate_access.client_id = access.client_id
                                          AND duplicate_access.client_user_id = access.client_user_id
                                          AND duplicate_access.admin_user_id = access.admin_user_id
                                          AND duplicate_access.conversation_id <> access.conversation_id)
                                   THEN 'duplicate_active_pair'
                               WHEN review.conversation_id IS NOT NULL
                                    AND (review.client_id IS DISTINCT FROM access.client_id
                                         OR review.review_state NOT IN ('NeedsReview', 'Resolved')
                                         OR (review.review_state = 'NeedsReview'
                                             AND (review.remediation_code IS DISTINCT FROM 'confirm_exact_client_and_admin_participants'
                                                  OR review.resolved_at IS NOT NULL
                                                  OR review.resolved_by_admin_user_id IS NOT NULL))
                                         OR (review.review_state = 'Resolved'
                                             AND (review.remediation_code IS DISTINCT FROM 'participants_confirmed'
                                                  OR review.resolved_at IS NULL
                                                  OR review.resolved_by_admin_user_id IS NULL)))
                                   THEN 'conflicting_review_state'
                               WHEN access.state = 'NeedsReview'
                                    AND review.conversation_id IS NOT NULL
                                    AND review.review_state IS DISTINCT FROM 'NeedsReview'
                                   THEN 'conflicting_review_state'
                               WHEN access.state IN ('Active', 'Archived')
                                    AND NOT EXISTS (
                                        SELECT 1
                                        FROM digital.conversation_audit AS approval_audit
                                        WHERE approval_audit.conversation_id = access.conversation_id
                                          AND approval_audit.client_id = access.client_id
                                          AND approval_audit.action = 'LegacyPrivateApproved'
                                          AND approval_audit.actor_kind = 'Admin'
                                          AND approval_audit.admin_user_id IS NOT NULL
                                          AND (review.conversation_id IS NULL
                                               OR approval_audit.admin_user_id = review.resolved_by_admin_user_id)
                                          AND approval_audit.details->>'clientUserId' = access.client_user_id::text
                                          AND approval_audit.details->>'adminUserId' = access.admin_user_id::text
                                          AND NULLIF(btrim(approval_audit.details->>'reason'), '') IS NOT NULL)
                                   THEN 'missing_approval_evidence'
                               ELSE NULL
                           END AS failed_predicate
                    FROM digital.instructions AS root
                    JOIN digital.conversation_access AS access
                      ON access.conversation_id = root.id
                    LEFT JOIN digital.private_conversation_review AS review
                      ON review.conversation_id = access.conversation_id
                    WHERE root.instruction_id = root.id
                      AND root.inst_type_id = 101
                ) AS evaluated
                WHERE evaluated.failed_predicate IS NOT NULL
                ORDER BY evaluated.conversation_id
                LIMIT 1
            $private_mapping_guard_query$
            INTO conflict_conversation_id,
                 conflict_client_id,
                 conflict_kind,
                 conflict_state,
                 failed_predicate;
        ELSE
            SELECT evaluated.conversation_id,
                   evaluated.client_id,
                   evaluated.conversation_kind,
                   evaluated.state,
                   evaluated.failed_predicate
            INTO conflict_conversation_id,
                 conflict_client_id,
                 conflict_kind,
                 conflict_state,
                 failed_predicate
            FROM (
                SELECT root.id AS conversation_id,
                       root.client_id,
                       access.conversation_kind,
                       access.state,
                       CASE
                           WHEN access.conversation_kind IS DISTINCT FROM 'Private'
                               THEN 'not_private_kind'
                           WHEN access.client_id IS DISTINCT FROM root.client_id
                               THEN 'tenant_mismatch'
                           WHEN access.state NOT IN ('NeedsReview', 'Active', 'Archived')
                               THEN 'invalid_lifecycle_fields'
                           WHEN access.client_user_id IS NOT NULL
                                AND NOT EXISTS (
                                    SELECT 1
                                    FROM internal.support_users AS client_user
                                    WHERE client_user.id = access.client_user_id
                                      AND client_user.client_id::bigint = access.client_id
                                      AND client_user.status IS TRUE
                                      AND client_user.deactive_date IS NULL)
                               THEN 'invalid_client_participant'
                           WHEN access.admin_user_id IS NOT NULL
                                AND NOT EXISTS (
                                    SELECT 1
                                    FROM admin.users AS admin_user
                                WHERE admin_user.id = access.admin_user_id
                                  AND admin_user.status IS TRUE
                                  AND admin_user.deactive_date IS NULL)
                               THEN 'invalid_admin_participant'
                           WHEN access.state = 'NeedsReview'
                                AND access.archived_at IS NOT NULL
                               THEN 'invalid_lifecycle_fields'
                           WHEN access.state = 'Active'
                                AND access.archived_at IS NOT NULL
                               THEN 'invalid_lifecycle_fields'
                           WHEN access.state = 'Archived'
                                AND access.archived_at IS NULL
                               THEN 'invalid_lifecycle_fields'
                           WHEN access.state IN ('Active', 'Archived')
                                AND (access.client_user_id IS NULL OR access.admin_user_id IS NULL)
                               THEN 'invalid_lifecycle_fields'
                           WHEN access.state = 'Active'
                                AND EXISTS (
                                    SELECT 1
                                    FROM digital.conversation_access AS duplicate_access
                                    WHERE duplicate_access.conversation_kind = 'Private'
                                      AND duplicate_access.state = 'Active'
                                      AND duplicate_access.client_id = access.client_id
                                      AND duplicate_access.client_user_id = access.client_user_id
                                      AND duplicate_access.admin_user_id = access.admin_user_id
                                      AND duplicate_access.conversation_id <> access.conversation_id)
                               THEN 'duplicate_active_pair'
                           WHEN access.state IN ('Active', 'Archived')
                                AND NOT EXISTS (
                                    SELECT 1
                                    FROM digital.conversation_audit AS approval_audit
                                    WHERE approval_audit.conversation_id = access.conversation_id
                                      AND approval_audit.client_id = access.client_id
                                      AND approval_audit.action = 'LegacyPrivateApproved'
                                      AND approval_audit.actor_kind = 'Admin'
                                      AND approval_audit.admin_user_id IS NOT NULL
                                      AND approval_audit.details->>'clientUserId' = access.client_user_id::text
                                      AND approval_audit.details->>'adminUserId' = access.admin_user_id::text
                                      AND NULLIF(btrim(approval_audit.details->>'reason'), '') IS NOT NULL)
                               THEN 'missing_approval_evidence'
                           ELSE NULL
                       END AS failed_predicate
                FROM digital.instructions AS root
                JOIN digital.conversation_access AS access
                  ON access.conversation_id = root.id
                WHERE root.instruction_id = root.id
                  AND root.inst_type_id = 101
            ) AS evaluated
            WHERE evaluated.failed_predicate IS NOT NULL
            ORDER BY evaluated.conversation_id
            LIMIT 1;
        END IF;

        IF conflict_conversation_id IS NOT NULL THEN
            RAISE EXCEPTION
                'Legacy Private mapping conflict: conversation_id=%, client_id=%, kind=%, state=%, failed_predicate=%',
                conflict_conversation_id,
                conflict_client_id,
                conflict_kind,
                conflict_state,
                failed_predicate;
        END IF;
    END;

-- Validate tenant and private-participant invariants before enforcing them.
    IF EXISTS (
        SELECT 1
        FROM digital.conversation_access AS access
        JOIN digital.instructions AS root ON root.id = access.conversation_id
        WHERE access.client_id IS DISTINCT FROM root.client_id
    ) THEN
        RAISE EXCEPTION 'conversation_access tenant differs from its root';
    END IF;

    IF EXISTS (
        SELECT 1
        FROM digital.conversation_access AS access
        LEFT JOIN internal.support_users AS client_user
          ON client_user.id = access.client_user_id
        WHERE access.conversation_kind = 'Private'
          AND access.client_user_id IS NOT NULL
          AND (client_user.id IS NULL
               OR client_user.client_id::bigint IS DISTINCT FROM access.client_id
               OR client_user.status IS DISTINCT FROM TRUE
               OR client_user.deactive_date IS NOT NULL)
    ) THEN
        RAISE EXCEPTION 'Private access contains a missing or cross-tenant Client participant';
    END IF;

    IF EXISTS (
        SELECT 1
        FROM digital.conversation_access AS access
        LEFT JOIN admin.users AS admin_user
          ON admin_user.id = access.admin_user_id
        WHERE access.conversation_kind = 'Private'
          AND access.admin_user_id IS NOT NULL
          AND (admin_user.id IS NULL
               OR admin_user.status IS DISTINCT FROM TRUE
               OR admin_user.deactive_date IS NOT NULL)
    ) THEN
        RAISE EXCEPTION 'Private access contains a missing Admin participant';
    END IF;

    IF EXISTS (
        SELECT 1
        FROM digital.conversation_access
        WHERE conversation_kind = 'Private'
          AND state IN ('Active', 'Archived')
          AND (client_user_id IS NULL OR admin_user_id IS NULL)
    ) THEN
        RAISE EXCEPTION 'Active or archived Private access is missing a required participant';
    END IF;

    IF EXISTS (
        SELECT 1
        FROM digital.conversation_access
        WHERE conversation_kind = 'Private'
          AND state = 'Active'
        GROUP BY client_id, client_user_id, admin_user_id
        HAVING count(*) > 1
    ) THEN
        RAISE EXCEPTION 'Duplicate active Private participant pair requires manual remediation';
    END IF;

    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint AS constraint_row
        WHERE constraint_row.conrelid = 'digital.conversation_access'::regclass
          AND constraint_row.contype = 'f'
          AND constraint_row.confrelid = 'internal.support_users'::regclass
          AND constraint_row.conkey = ARRAY[
              (SELECT attnum FROM pg_attribute
               WHERE attrelid = 'digital.conversation_access'::regclass
                 AND attname = 'client_user_id'
                 AND NOT attisdropped)
          ]::smallint[]
    ) THEN
        RAISE EXCEPTION 'conversation_access is missing its required Client participant foreign key';
    END IF;

    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint AS constraint_row
        WHERE constraint_row.conrelid = 'digital.conversation_access'::regclass
          AND constraint_row.contype = 'f'
          AND constraint_row.confrelid = 'admin.users'::regclass
          AND constraint_row.conkey = ARRAY[
              (SELECT attnum FROM pg_attribute
               WHERE attrelid = 'digital.conversation_access'::regclass
                 AND attname = 'admin_user_id'
                 AND NOT attisdropped)
          ]::smallint[]
    ) THEN
        RAISE EXCEPTION 'conversation_access is missing its required Admin participant foreign key';
    END IF;
END
$legacy_private_mapping_guard$;

-- Add composite keys only after the guard proves existing rows satisfy them.
DO $legacy_private_mapping_constraints$
BEGIN
    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conrelid = 'digital.instructions'::regclass
          AND conname = 'uq_instructions_id_client_id'
    ) THEN
        ALTER TABLE digital.instructions
            ADD CONSTRAINT uq_instructions_id_client_id UNIQUE (id, client_id);
    END IF;

    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conrelid = 'digital.conversation_access'::regclass
          AND conname = 'uq_conversation_access_conversation_client'
    ) THEN
        ALTER TABLE digital.conversation_access
            ADD CONSTRAINT uq_conversation_access_conversation_client
                UNIQUE (conversation_id, client_id);
    END IF;

    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conrelid = 'digital.conversation_access'::regclass
          AND conname = 'fk_conversation_access_conversation_tenant'
    ) THEN
        ALTER TABLE digital.conversation_access
            ADD CONSTRAINT fk_conversation_access_conversation_tenant
                FOREIGN KEY (conversation_id, client_id)
                REFERENCES digital.instructions (id, client_id);
    END IF;
END
$legacy_private_mapping_constraints$;

CREATE UNIQUE INDEX IF NOT EXISTS ix_conversation_access_active_private_pair_unique
ON digital.conversation_access (client_id, client_user_id, admin_user_id)
WHERE conversation_kind = 'Private' AND state = 'Active';

CREATE TABLE IF NOT EXISTS digital.private_conversation_review (
    conversation_id bigint NOT NULL,
    client_id bigint NOT NULL,
    review_state varchar(16) NOT NULL DEFAULT 'NeedsReview',
    remediation_code varchar(64) NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    resolved_at timestamptz,
    resolved_by_admin_user_id integer,
    CONSTRAINT pk_private_conversation_review PRIMARY KEY (conversation_id),
    CONSTRAINT fk_private_conversation_review_access_tenant
        FOREIGN KEY (conversation_id, client_id)
        REFERENCES digital.conversation_access (conversation_id, client_id),
    CONSTRAINT fk_private_conversation_review_resolved_by_admin
        FOREIGN KEY (resolved_by_admin_user_id) REFERENCES admin.users(id),
    CONSTRAINT ck_private_conversation_review_state
        CHECK (review_state IN ('NeedsReview', 'Resolved')),
    CONSTRAINT ck_private_conversation_review_remediation
        CHECK (remediation_code IN (
            'confirm_exact_client_and_admin_participants',
            'participants_confirmed')),
    CONSTRAINT ck_private_conversation_review_resolution
        CHECK (
            (review_state = 'NeedsReview'
                AND remediation_code = 'confirm_exact_client_and_admin_participants'
                AND resolved_at IS NULL
                AND resolved_by_admin_user_id IS NULL)
            OR
            (review_state = 'Resolved'
                AND remediation_code = 'participants_confirmed'
                AND resolved_at IS NOT NULL
                AND resolved_by_admin_user_id IS NOT NULL)
        )
);

COMMENT ON TABLE digital.private_conversation_review IS
    'Content-free evidence and remediation state for legacy Private mappings. A NeedsReview row blocks Private messaging activation.';
COMMENT ON COLUMN digital.private_conversation_review.remediation_code IS
    'NeedsReview requires an Admin to confirm the exact tenant-valid Client and active Admin participants; no message text is stored.';

-- Insert canonical roots only; preserve NeedsReview and approved mappings on rerun.
INSERT INTO digital.conversation_access (
    conversation_id, client_id, conversation_kind, state, version, created_at)
SELECT root.id,
       root.client_id,
       'Private',
       'NeedsReview',
       1,
       COALESCE(root.datetime, root.insert_date)
FROM digital.instructions AS root
WHERE root.instruction_id = root.id
  AND root.inst_type_id = 101
ON CONFLICT (conversation_id) DO NOTHING;

-- Reconstruct readiness only from exact repository approval evidence.
WITH resolved_reviews AS (
    INSERT INTO digital.private_conversation_review (
        conversation_id,
        client_id,
        review_state,
        remediation_code,
        created_at,
        resolved_at,
        resolved_by_admin_user_id)
    SELECT access.conversation_id,
           access.client_id,
           'Resolved',
           'participants_confirmed',
           access.created_at,
           approval.occurred_at,
           approval.admin_user_id
    FROM digital.conversation_access AS access
    JOIN digital.instructions AS root
      ON root.id = access.conversation_id
    LEFT JOIN digital.private_conversation_review AS existing_review
      ON existing_review.conversation_id = access.conversation_id
    JOIN LATERAL (
        SELECT approval_audit.occurred_at,
               approval_audit.admin_user_id
        FROM digital.conversation_audit AS approval_audit
        WHERE approval_audit.conversation_id = access.conversation_id
          AND approval_audit.client_id = access.client_id
          AND approval_audit.action = 'LegacyPrivateApproved'
          AND approval_audit.actor_kind = 'Admin'
          AND approval_audit.admin_user_id IS NOT NULL
          AND approval_audit.details->>'clientUserId' = access.client_user_id::text
          AND approval_audit.details->>'adminUserId' = access.admin_user_id::text
          AND NULLIF(btrim(approval_audit.details->>'reason'), '') IS NOT NULL
        ORDER BY approval_audit.occurred_at, approval_audit.audit_id
        LIMIT 1
    ) AS approval ON TRUE
    WHERE root.instruction_id = root.id
      AND root.inst_type_id = 101
      AND access.conversation_kind = 'Private'
      AND access.state IN ('Active', 'Archived')
      AND access.client_user_id IS NOT NULL
      AND access.admin_user_id IS NOT NULL
      AND existing_review.conversation_id IS NULL
    ON CONFLICT (conversation_id) DO NOTHING
    RETURNING conversation_id, client_id, created_at, resolved_at,
              resolved_by_admin_user_id
)
INSERT INTO digital.conversation_audit (
    conversation_id,
    client_id,
    action,
    actor_kind,
    occurred_at,
    details)
SELECT review.conversation_id,
       review.client_id,
       'LegacyPrivateReviewReconciled',
       'System',
       review.resolved_at,
       jsonb_build_object(
           'migration', '202608051200_complete_legacy_private_mapping_gate',
           'remediationCode', 'participants_confirmed',
           'approvalEvidence', 'LegacyPrivateApproved')
FROM resolved_reviews AS review;

WITH inserted_reviews AS (
    INSERT INTO digital.private_conversation_review (
        conversation_id, client_id, review_state, remediation_code, created_at)
    SELECT access.conversation_id,
           access.client_id,
           'NeedsReview',
           'confirm_exact_client_and_admin_participants',
           access.created_at
    FROM digital.conversation_access AS access
    JOIN digital.instructions AS root ON root.id = access.conversation_id
    WHERE root.instruction_id = root.id
      AND root.inst_type_id = 101
      AND access.conversation_kind = 'Private'
      AND access.state = 'NeedsReview'
    ON CONFLICT (conversation_id) DO NOTHING
    RETURNING conversation_id, client_id, created_at
)
INSERT INTO digital.conversation_audit (
    conversation_id, client_id, action, actor_kind, occurred_at, details)
SELECT review.conversation_id,
       review.client_id,
       'LegacyPrivateReviewRecorded',
       'System',
       review.created_at,
       jsonb_build_object(
           'migration', '202608051200_complete_legacy_private_mapping_gate',
           'remediationCode', 'confirm_exact_client_and_admin_participants')
FROM inserted_reviews AS review;

CREATE OR REPLACE FUNCTION digital.enforce_private_conversation_participant_tenant()
RETURNS trigger
LANGUAGE plpgsql
AS $function$
BEGIN
    IF NEW.conversation_kind = 'Private'
       AND NEW.client_user_id IS NOT NULL
       AND NOT EXISTS (
           SELECT 1
           FROM internal.support_users AS client_user
           WHERE client_user.id = NEW.client_user_id
             AND client_user.client_id::bigint = NEW.client_id)
    THEN
        RAISE EXCEPTION 'Private Client participant % does not belong to tenant %',
            NEW.client_user_id, NEW.client_id;
    END IF;
    RETURN NEW;
END
$function$;

DO $legacy_private_mapping_participant_trigger$
BEGIN
    IF NOT EXISTS (
        SELECT 1
        FROM pg_trigger
        WHERE tgrelid = 'digital.conversation_access'::regclass
          AND tgname = 'trg_conversation_access_private_participant_tenant'
          AND NOT tgisinternal
    ) THEN
        CREATE TRIGGER trg_conversation_access_private_participant_tenant
        BEFORE INSERT OR UPDATE OF conversation_kind, client_id, client_user_id
        ON digital.conversation_access
        FOR EACH ROW
        EXECUTE FUNCTION digital.enforce_private_conversation_participant_tenant();
    END IF;
END
$legacy_private_mapping_participant_trigger$;

-- A deferred trigger permits either statement order and rejects committed mismatch.
CREATE OR REPLACE FUNCTION digital.enforce_private_conversation_review_state()
RETURNS trigger
LANGUAGE plpgsql
AS $function$
DECLARE
    target_conversation_id bigint := COALESCE(NEW.conversation_id, OLD.conversation_id);
    access_state varchar(16);
    access_kind varchar(16);
    review_state_value varchar(16);
BEGIN
    SELECT conversation_kind, state
      INTO access_kind, access_state
      FROM digital.conversation_access
     WHERE conversation_id = target_conversation_id;

    SELECT review_state
      INTO review_state_value
      FROM digital.private_conversation_review
     WHERE conversation_id = target_conversation_id;

    IF review_state_value IS NOT NULL
       AND (access_kind IS DISTINCT FROM 'Private'
            OR NOT (
                (review_state_value = 'NeedsReview' AND access_state = 'NeedsReview')
                OR (review_state_value = 'Resolved' AND access_state IN ('Active', 'Archived'))))
    THEN
        RAISE EXCEPTION 'Private review state does not match access state for conversation %',
            target_conversation_id;
    END IF;
    RETURN NULL;
END
$function$;

DO $legacy_private_mapping_review_triggers$
BEGIN
    IF NOT EXISTS (
        SELECT 1
        FROM pg_trigger
        WHERE tgrelid = 'digital.private_conversation_review'::regclass
          AND tgname = 'trg_private_conversation_review_matches_access'
          AND NOT tgisinternal
    ) THEN
        CREATE CONSTRAINT TRIGGER trg_private_conversation_review_matches_access
        AFTER INSERT OR UPDATE OR DELETE ON digital.private_conversation_review
        DEFERRABLE INITIALLY DEFERRED
        FOR EACH ROW EXECUTE FUNCTION digital.enforce_private_conversation_review_state();
    END IF;

    IF NOT EXISTS (
        SELECT 1
        FROM pg_trigger
        WHERE tgrelid = 'digital.conversation_access'::regclass
          AND tgname = 'trg_conversation_access_matches_private_review'
          AND NOT tgisinternal
    ) THEN
        CREATE CONSTRAINT TRIGGER trg_conversation_access_matches_private_review
        AFTER UPDATE OF conversation_kind, state ON digital.conversation_access
        DEFERRABLE INITIALLY DEFERRED
        FOR EACH ROW EXECUTE FUNCTION digital.enforce_private_conversation_review_state();
    END IF;
END
$legacy_private_mapping_review_triggers$;
