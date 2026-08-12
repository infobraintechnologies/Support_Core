using Dapper;
using Npgsql;

namespace CBSSupport.API.Configuration;

public sealed record PrivateMessagingReadiness(
    bool IsReady,
    long NeedsReviewCount,
    long InvalidCount,
    string Status);

public interface IPrivateMessagingReadinessGate
{
    Task<PrivateMessagingReadiness> CheckAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Checks only metadata needed to decide whether Private messaging may be enabled.
/// It intentionally never selects legacy instruction/message text.
/// </summary>
public sealed class PrivateMessagingReadinessGate(string connectionString)
    : IPrivateMessagingReadinessGate
{
    public async Task<PrivateMessagingReadiness> CheckAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        var schemaReady = await connection.QuerySingleAsync<bool>(new CommandDefinition(
            """
            SELECT to_regclass('digital.private_conversation_review') IS NOT NULL
               AND to_regclass('digital.conversation_access') IS NOT NULL;
            """,
            cancellationToken: cancellationToken));
        if (!schemaReady)
        {
            return new(false, 0, 1, "NotReady: legacy Private review schema is not deployed");
        }

        var counts = await connection.QuerySingleAsync<ReadinessCounts>(new CommandDefinition(
            """
            WITH legacy_roots AS (
                SELECT id AS conversation_id, client_id AS expected_client_id
                FROM digital.instructions
                WHERE instruction_id = id
                  AND inst_type_id = 101
            ),
            classified AS (
                SELECT CASE
                    WHEN access.conversation_id IS NULL THEN 'roots_without_access_row'
                    WHEN access.conversation_kind IS DISTINCT FROM 'Private' THEN 'not_private_kind'
                    WHEN access.client_id IS DISTINCT FROM root.expected_client_id THEN 'tenant_mismatch'
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
                         OR (review.review_state = 'Resolved' AND (
                              access.state NOT IN ('Active', 'Archived')
                              OR review.remediation_code IS DISTINCT FROM 'participants_confirmed'
                              OR review.resolved_at IS NULL
                              OR review.resolved_by_admin_user_id IS NULL))
                         OR review.review_state NOT IN ('NeedsReview', 'Resolved'))
                        THEN 'conflicting_review_state'
                    WHEN access.state = 'NeedsReview' AND review.review_state = 'NeedsReview'
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
                FROM legacy_roots root
                LEFT JOIN digital.conversation_access access
                  ON access.conversation_id = root.conversation_id
                LEFT JOIN digital.private_conversation_review review
                  ON review.conversation_id = access.conversation_id
            ),
            all_rows AS (
                SELECT status_code FROM classified
                UNION ALL
                SELECT 'conflicting_review_state'
                FROM digital.conversation_access access
                LEFT JOIN digital.instructions root ON root.id = access.conversation_id
                WHERE access.conversation_kind = 'Private'
                  AND (root.id IS NULL OR root.instruction_id IS DISTINCT FROM root.id
                       OR root.inst_type_id IS DISTINCT FROM 101)
            )
            SELECT count(*) FILTER (WHERE status_code = 'unresolved_needs_review') AS NeedsReviewCount,
                   count(*) FILTER (WHERE status_code NOT IN (
                       'unresolved_needs_review',
                       'valid_resolved_active',
                       'valid_resolved_archived',
                       'valid_v2_active',
                       'valid_v2_archived')) AS InvalidCount
            FROM all_rows;
            """,
            cancellationToken: cancellationToken));

        var ready = counts.NeedsReviewCount == 0 && counts.InvalidCount == 0;
        return new(
            ready,
            counts.NeedsReviewCount,
            counts.InvalidCount,
            ready
                ? "Ready"
                : "NotReady: resolve every legacy Private review row before enabling Private messaging");
    }

    private sealed record ReadinessCounts(long NeedsReviewCount, long InvalidCount);
}
