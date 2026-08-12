-- CBS Support read-only deployment preflight
-- Reports only IDs, tenant/participant IDs, states, review status, and remediation
-- codes. It never reads or returns instruction/message content.
-- Run after 202608051200_complete_legacy_private_mapping_gate.

-- 1. Content-free row classification.
WITH legacy_roots AS (
    SELECT id AS conversation_id, client_id AS expected_client_id
    FROM digital.instructions
    WHERE instruction_id = id
      AND inst_type_id = 101
),
base AS (
    SELECT root.conversation_id,
           root.expected_client_id,
           access.client_id,
           access.conversation_kind,
           access.state,
           access.client_user_id,
           access.admin_user_id,
           access.archived_at,
           review.review_state,
           review.client_id AS review_client_id,
           review.remediation_code,
           review.resolved_at,
           review.resolved_by_admin_user_id,
           access.conversation_id IS NOT NULL AS has_access_row,
           (access.client_user_id IS NULL OR (
               client_user.id IS NOT NULL
               AND client_user.client_id::bigint = access.client_id
               AND client_user.status IS TRUE
               AND client_user.deactive_date IS NULL
           )) AS valid_client_participant,
           (access.admin_user_id IS NULL OR (
               admin_user.id IS NOT NULL
               AND admin_user.status IS TRUE
               AND admin_user.deactive_date IS NULL
           ))
               AS valid_admin_participant,
           COALESCE((
               SELECT count(*)
               FROM digital.conversation_audit audit
                WHERE audit.conversation_id = access.conversation_id
                  AND audit.client_id = access.client_id
                  AND audit.action = 'LegacyPrivateApproved'
                  AND audit.actor_kind = 'Admin'
                  AND audit.admin_user_id IS NOT NULL
                  AND (review.conversation_id IS NULL
                       OR audit.admin_user_id = review.resolved_by_admin_user_id)
                  AND audit.details->>'clientUserId' = access.client_user_id::text
                 AND audit.details->>'adminUserId' = access.admin_user_id::text
                 AND NULLIF(btrim(audit.details->>'reason'), '') IS NOT NULL
           ), 0)::bigint AS approval_audit_count,
           access.state = 'Active'
               AND EXISTS (
                   SELECT 1
                   FROM digital.conversation_access duplicate_access
                   WHERE duplicate_access.conversation_kind = 'Private'
                     AND duplicate_access.state = 'Active'
                     AND duplicate_access.client_id = access.client_id
                     AND duplicate_access.client_user_id = access.client_user_id
                     AND duplicate_access.admin_user_id = access.admin_user_id
                     AND duplicate_access.conversation_id <> access.conversation_id
               ) AS duplicate_active_pair
    FROM legacy_roots root
    LEFT JOIN digital.conversation_access access
      ON access.conversation_id = root.conversation_id
    LEFT JOIN digital.private_conversation_review review
      ON review.conversation_id = access.conversation_id
    LEFT JOIN internal.support_users client_user
      ON client_user.id = access.client_user_id
    LEFT JOIN admin.users admin_user
      ON admin_user.id = access.admin_user_id
),
classified AS (
    SELECT base.*,
           CASE
               WHEN NOT has_access_row THEN 'roots_without_access_row'
               WHEN conversation_kind IS DISTINCT FROM 'Private' THEN 'not_private_kind'
               WHEN client_id IS DISTINCT FROM expected_client_id THEN 'tenant_mismatch'
               WHEN client_user_id IS NOT NULL AND NOT valid_client_participant
                   THEN 'invalid_client_participant'
               WHEN admin_user_id IS NOT NULL AND NOT valid_admin_participant
                   THEN 'invalid_admin_participant'
               WHEN state NOT IN ('NeedsReview', 'Active', 'Archived')
                 OR (state = 'NeedsReview' AND archived_at IS NOT NULL)
                 OR (state = 'Active' AND archived_at IS NOT NULL)
                 OR (state = 'Archived' AND archived_at IS NULL)
                 OR (state IN ('Active', 'Archived')
                     AND (client_user_id IS NULL OR admin_user_id IS NULL))
                   THEN 'invalid_lifecycle_fields'
               WHEN duplicate_active_pair THEN 'duplicate_active_pair'
               WHEN review_client_id IS NOT NULL
                    AND review_client_id IS DISTINCT FROM client_id
                   THEN 'conflicting_review_state'
               WHEN review_state IS NOT NULL AND (
                    (review_state = 'NeedsReview' AND (
                         state IS DISTINCT FROM 'NeedsReview'
                         OR remediation_code IS DISTINCT FROM 'confirm_exact_client_and_admin_participants'
                         OR resolved_at IS NOT NULL
                         OR resolved_by_admin_user_id IS NOT NULL))
                    OR
                    (review_state = 'Resolved' AND (
                         state NOT IN ('Active', 'Archived')
                         OR remediation_code IS DISTINCT FROM 'participants_confirmed'
                         OR resolved_at IS NULL
                         OR resolved_by_admin_user_id IS NULL))
                    OR review_state NOT IN ('NeedsReview', 'Resolved'))
                   THEN 'conflicting_review_state'
               WHEN state = 'NeedsReview' AND review_state = 'NeedsReview'
                   THEN 'unresolved_needs_review'
               WHEN state IN ('Active', 'Archived') AND NOT (
                    (
                        review_state = 'Resolved'
                        AND remediation_code = 'participants_confirmed'
                        AND resolved_at IS NOT NULL
                        AND resolved_by_admin_user_id IS NOT NULL
                        AND approval_audit_count > 0
                    )
                    OR (
                        review_state IS NULL
                        AND EXISTS (
                            SELECT 1
                            FROM digital.conversation_audit AS creation_audit
                            WHERE creation_audit.conversation_id = base.conversation_id
                              AND creation_audit.client_id = base.client_id
                              AND creation_audit.action = 'Created'
                              AND creation_audit.details->>'clientUserId' = base.client_user_id::text
                              AND creation_audit.details->>'adminUserId' = base.admin_user_id::text
                        )
                    ))
                   THEN 'resolved_without_approval_evidence'
               WHEN state = 'Active' AND review_state IS NULL THEN 'valid_v2_active'
               WHEN state = 'Archived' AND review_state IS NULL THEN 'valid_v2_archived'
               WHEN state = 'Active' THEN 'valid_resolved_active'
               WHEN state = 'Archived' THEN 'valid_resolved_archived'
               ELSE 'conflicting_review_state'
           END AS status_code
    FROM base
),
stray_private AS (
    SELECT access.conversation_id,
           NULL::bigint AS expected_client_id,
           access.client_id,
           access.conversation_kind,
           access.state,
           access.client_user_id,
           access.admin_user_id,
           NULL::varchar(16) AS review_state,
           0::bigint AS approval_audit_count,
           'conflicting_review_state'::text AS status_code
    FROM digital.conversation_access access
    LEFT JOIN legacy_roots root ON root.conversation_id = access.conversation_id
    WHERE access.conversation_kind = 'Private'
      AND root.conversation_id IS NULL
)
SELECT 'LegacyRoot'::text AS source,
       conversation_id,
       expected_client_id,
       client_id,
       conversation_kind,
       state,
       client_user_id,
       admin_user_id,
       review_state,
       approval_audit_count,
       status_code
FROM classified
UNION ALL
SELECT 'StrayPrivateAccess',
       conversation_id,
       expected_client_id,
       client_id,
       conversation_kind,
       state,
       client_user_id,
       admin_user_id,
       review_state,
       approval_audit_count,
       status_code
FROM stray_private
ORDER BY status_code, client_id NULLS FIRST, conversation_id;

-- 2. Deterministic status counts and readiness result.
WITH status_rows AS (
    SELECT status_code, count(*)::bigint AS row_count
    FROM (
        SELECT CASE
                   WHEN access.conversation_id IS NULL THEN 'roots_without_access_row'
                   WHEN access.conversation_kind IS DISTINCT FROM 'Private' THEN 'not_private_kind'
                   WHEN access.client_id IS DISTINCT FROM root.client_id THEN 'tenant_mismatch'
                   WHEN access.client_user_id IS NOT NULL AND NOT EXISTS (
                       SELECT 1 FROM internal.support_users client_user
                        WHERE client_user.id = access.client_user_id
                         AND client_user.client_id::bigint = access.client_id
                         AND client_user.status IS TRUE
                         AND client_user.deactive_date IS NULL)
                       THEN 'invalid_client_participant'
                   WHEN access.admin_user_id IS NOT NULL AND NOT EXISTS (
                        SELECT 1 FROM admin.users admin_user
                       WHERE admin_user.id = access.admin_user_id
                         AND admin_user.status IS TRUE
                         AND admin_user.deactive_date IS NULL)
                       THEN 'invalid_admin_participant'
                   WHEN access.state NOT IN ('NeedsReview', 'Active', 'Archived')
                     OR (access.state = 'NeedsReview' AND access.archived_at IS NOT NULL)
                     OR (access.state = 'Active' AND access.archived_at IS NOT NULL)
                     OR (access.state = 'Archived' AND access.archived_at IS NULL)
                     OR (access.state IN ('Active', 'Archived')
                         AND (access.client_user_id IS NULL OR access.admin_user_id IS NULL))
                       THEN 'invalid_lifecycle_fields'
                   WHEN access.state = 'Active' AND EXISTS (
                       SELECT 1 FROM digital.conversation_access duplicate_access
                       WHERE duplicate_access.conversation_kind = 'Private'
                         AND duplicate_access.state = 'Active'
                         AND duplicate_access.client_id = access.client_id
                         AND duplicate_access.client_user_id = access.client_user_id
                         AND duplicate_access.admin_user_id = access.admin_user_id
                         AND duplicate_access.conversation_id <> access.conversation_id)
                       THEN 'duplicate_active_pair'
                   WHEN review.client_id IS NOT NULL
                        AND review.client_id IS DISTINCT FROM access.client_id
                       THEN 'conflicting_review_state'
                   WHEN review.review_state IS NOT NULL AND (
                        (review.review_state = 'NeedsReview' AND (
                             access.state IS DISTINCT FROM 'NeedsReview'
                             OR review.remediation_code IS DISTINCT FROM 'confirm_exact_client_and_admin_participants'
                             OR review.resolved_at IS NOT NULL
                             OR review.resolved_by_admin_user_id IS NOT NULL))
                        OR
                        (review.review_state = 'Resolved' AND (
                             access.state NOT IN ('Active', 'Archived')
                             OR review.remediation_code IS DISTINCT FROM 'participants_confirmed'
                             OR review.resolved_at IS NULL
                             OR review.resolved_by_admin_user_id IS NULL))
                        OR review.review_state NOT IN ('NeedsReview', 'Resolved'))
                       THEN 'conflicting_review_state'
                   WHEN access.state = 'NeedsReview'
                        AND review.review_state = 'NeedsReview'
                       THEN 'unresolved_needs_review'
                   WHEN access.state IN ('Active', 'Archived') AND NOT (
                        (
                            review.review_state = 'Resolved'
                            AND review.remediation_code = 'participants_confirmed'
                            AND review.resolved_at IS NOT NULL
                            AND review.resolved_by_admin_user_id IS NOT NULL
                            AND EXISTS (
                                SELECT 1
                                FROM digital.conversation_audit audit
                                WHERE audit.conversation_id = access.conversation_id
                                  AND audit.client_id = access.client_id
                                  AND audit.action = 'LegacyPrivateApproved'
                                  AND audit.actor_kind = 'Admin'
                                  AND audit.admin_user_id = review.resolved_by_admin_user_id
                                  AND audit.details->>'clientUserId' = access.client_user_id::text
                                  AND audit.details->>'adminUserId' = access.admin_user_id::text
                                  AND NULLIF(btrim(audit.details->>'reason'), '') IS NOT NULL)
                        )
                        OR (
                            review.conversation_id IS NULL
                            AND EXISTS (
                                SELECT 1
                                FROM digital.conversation_audit audit
                                WHERE audit.conversation_id = access.conversation_id
                                  AND audit.client_id = access.client_id
                                  AND audit.action = 'Created'
                                  AND audit.details->>'clientUserId' = access.client_user_id::text
                                  AND audit.details->>'adminUserId' = access.admin_user_id::text)
                        ))
                       THEN 'resolved_without_approval_evidence'
                   WHEN access.state = 'Active' AND review.conversation_id IS NULL THEN 'valid_v2_active'
                   WHEN access.state = 'Archived' AND review.conversation_id IS NULL THEN 'valid_v2_archived'
                   WHEN access.state = 'Active' THEN 'valid_resolved_active'
                   WHEN access.state = 'Archived' THEN 'valid_resolved_archived'
                   ELSE 'conflicting_review_state'
               END AS status_code
        FROM digital.instructions root
        LEFT JOIN digital.conversation_access access ON access.conversation_id = root.id
        LEFT JOIN digital.private_conversation_review review ON review.conversation_id = access.conversation_id
        WHERE root.instruction_id = root.id
          AND root.inst_type_id = 101
        UNION ALL
        SELECT 'conflicting_review_state'
        FROM digital.conversation_access access
        LEFT JOIN digital.instructions root ON root.id = access.conversation_id
        WHERE access.conversation_kind = 'Private'
          AND (root.id IS NULL OR root.instruction_id IS DISTINCT FROM root.id
               OR root.inst_type_id IS DISTINCT FROM 101)
    ) rows
    GROUP BY status_code
),
expected(status_code) AS (
    VALUES
        ('unresolved_needs_review'),
        ('valid_resolved_active'),
        ('valid_resolved_archived'),
        ('valid_v2_active'),
        ('valid_v2_archived'),
        ('resolved_without_approval_evidence'),
        ('not_private_kind'),
        ('tenant_mismatch'),
        ('invalid_client_participant'),
        ('invalid_admin_participant'),
        ('invalid_lifecycle_fields'),
        ('duplicate_active_pair'),
        ('conflicting_review_state'),
        ('roots_without_access_row')
),
counts AS (
    SELECT
        COALESCE(SUM(row_count) FILTER (WHERE status_code IN (
            'resolved_without_approval_evidence',
            'not_private_kind',
            'tenant_mismatch',
            'invalid_client_participant',
            'invalid_admin_participant',
            'invalid_lifecycle_fields',
            'duplicate_active_pair',
            'conflicting_review_state',
            'roots_without_access_row')), 0)::bigint AS invalid_count,
        COALESCE(SUM(row_count) FILTER (WHERE status_code = 'unresolved_needs_review'), 0)::bigint AS needs_review_count
    FROM status_rows
)
SELECT expected.status_code,
       COALESCE(status_rows.row_count, 0)::bigint AS row_count,
       NULL::boolean AS private_messaging_ready,
       NULL::bigint AS invalid_count,
       NULL::bigint AS needs_review_count,
       NULL::text AS status
FROM expected
LEFT JOIN status_rows USING (status_code)
UNION ALL
SELECT 'READY',
       NULL::bigint,
       counts.invalid_count = 0 AND counts.needs_review_count = 0,
       counts.invalid_count,
       counts.needs_review_count,
       CASE WHEN counts.invalid_count = 0 AND counts.needs_review_count = 0
            THEN 'Ready'
            ELSE 'NotReady: leave Messaging:Features:PrivateEnabled=false'
       END
FROM counts
ORDER BY status_code;
