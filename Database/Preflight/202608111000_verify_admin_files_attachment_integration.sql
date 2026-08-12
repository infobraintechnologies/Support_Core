-- Read-only admin.files compatibility and attachment projection check.
-- Run before/after 202608111010; archive the summary row.

WITH eligible AS MATERIALIZED (
    SELECT attachment.id,
           attachment.message_id,
           attachment.ready_key,
           attachment.display_name,
           attachment.client_id,
           attachment.admin_user_id,
           attachment.client_user_id,
           COALESCE(attachment.bound_at, attachment.ready_at, attachment.created_at) AS company_insert_date
    FROM digital.attachments attachment
    WHERE attachment.state = 'Ready'
      AND attachment.message_id IS NOT NULL
      AND attachment.ready_key IS NOT NULL
), classified AS MATERIALIZED (
    SELECT eligible.*,
           company_file.id AS company_file_id,
           company_file.table_name,
           company_file.table_id,
           company_file.file_name,
           company_file.old_file_name,
           company_file.file_desc,
           company_file.status,
           company_file.edited_file,
           company_file.remarks,
           company_file.insert_user,
           company_file.insert_date,
           company_file.edit_user,
           company_file.edit_date,
           company_file.id IS NOT NULL
             AND company_file.table_name = 'digital.instructions'
             AND company_file.table_id = eligible.message_id::text
             AND company_file.file_name = eligible.ready_key
             AND company_file.old_file_name IS NOT DISTINCT FROM eligible.display_name
             AND company_file.file_desc IS NOT DISTINCT FROM 'MESSAGE_ATTACHMENT'
             AND company_file.status IS TRUE
             AND company_file.edited_file IS NULL
             AND company_file.remarks IS NULL
             AND company_file.insert_user = COALESCE(
                    eligible.admin_user_id,
                    eligible.client_user_id) AS is_matching
    FROM eligible
    LEFT JOIN admin.files company_file ON company_file.id = eligible.id::text
), schema_compatibility AS MATERIALIZED (
    SELECT count(*) FILTER (
               WHERE actual.column_name IS NULL
                  OR actual.formatted_type <> expected.formatted_type
                  OR actual.is_not_null <> expected.is_not_null) AS incompatible_column_count
    FROM (VALUES
        ('id', 'character varying(50)', TRUE),
        ('table_name', 'character varying(50)', TRUE),
        ('table_id', 'character varying(50)', TRUE),
        ('file_name', 'character varying(50)', TRUE),
        ('old_file_name', 'text', FALSE),
        ('file_desc', 'character varying(50)', FALSE),
        ('status', 'boolean', TRUE),
        ('edited_file', 'character varying(50)', FALSE),
        ('remarks', 'text', FALSE),
        ('insert_user', 'integer', TRUE),
        ('insert_date', 'timestamp with time zone', TRUE),
        ('edit_user', 'integer', FALSE),
        ('edit_date', 'timestamp with time zone', FALSE)
    ) AS expected(column_name, formatted_type, is_not_null)
    LEFT JOIN (
        SELECT attribute.attname AS column_name,
               format_type(attribute.atttypid, attribute.atttypmod) AS formatted_type,
               attribute.attnotnull AS is_not_null
        FROM pg_attribute attribute
        JOIN pg_class relation ON relation.oid = attribute.attrelid
        JOIN pg_namespace namespace ON namespace.oid = relation.relnamespace
        WHERE namespace.nspname = 'admin'
          AND relation.relname = 'files'
          AND attribute.attnum > 0
          AND NOT attribute.attisdropped
    ) actual USING (column_name)
)
SELECT
    (SELECT incompatible_column_count FROM schema_compatibility)
        AS incompatible_admin_files_column_count,
    (SELECT count(*) FROM eligible) AS bound_ready_attachment_count,
    (SELECT count(*) FROM classified WHERE is_matching) AS matching_admin_files_count,
    (SELECT count(*) FROM classified WHERE company_file_id IS NULL) AS missing_admin_files_count,
    (SELECT count(*) FROM classified
     WHERE company_file_id IS NOT NULL AND NOT is_matching) AS conflicting_admin_files_count,
    (SELECT count(*)
     FROM admin.files company_file
     WHERE company_file.table_name = 'digital.instructions'
       AND NOT EXISTS (
            SELECT 1
            FROM digital.attachments attachment
            WHERE attachment.id::text = company_file.id)) AS orphan_admin_files_count,
    (SELECT count(*)
     FROM admin.files company_file
     WHERE company_file.table_name = 'digital.instructions'
       AND NOT EXISTS (
            SELECT 1
            FROM digital.instructions instruction
            WHERE instruction.id::text = company_file.table_id)) AS invalid_table_binding_count,
    (SELECT count(*) FROM classified
     WHERE company_file_id IS NOT NULL
       AND file_name IS DISTINCT FROM ready_key) AS filename_mismatch_count,
    (SELECT count(*) FROM classified
     WHERE company_file_id IS NOT NULL
       AND status IS NOT TRUE) AS inactive_status_mismatch_count,
    (SELECT count(*) FROM eligible WHERE length(ready_key) > 50)
        AS oversize_ready_key_count,
    (SELECT count(*)
     FROM eligible
     LEFT JOIN internal.support_users support_user
       ON support_user.id = eligible.client_user_id
      AND support_user.client_id::bigint = eligible.client_id
     LEFT JOIN admin.users mirrored_admin
       ON mirrored_admin.id = support_user.id
      AND mirrored_admin.user_name = support_user.user_name
     LEFT JOIN admin.users attachment_admin
       ON attachment_admin.id = eligible.admin_user_id
     WHERE (eligible.client_user_id IS NOT NULL AND mirrored_admin.id IS NULL)
        OR (eligible.admin_user_id IS NOT NULL AND attachment_admin.id IS NULL))
        AS invalid_user_domain_mapping_count,
    (SELECT count(*)
     FROM admin.files company_file
     JOIN digital.attachments attachment ON attachment.id::text = company_file.id
     WHERE attachment.message_id IS NULL) AS staged_admin_files_count;
