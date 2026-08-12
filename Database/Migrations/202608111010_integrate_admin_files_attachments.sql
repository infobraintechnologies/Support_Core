-- Project bound/available CBS Support attachments into admin.files.
-- migration-transaction: true
-- Preconditions: preflight clean; reviewed admin.files shape/FK matches; identity
-- mirrors exist; attachments remain disabled during migration and cutover.
-- Post-deployment: rerun the preflight with zero mismatch counts, then deploy the
-- binding-aware application. Preserve company history; use a forward-fix for defects.

DO $compatibility$
DECLARE
    incompatible_columns text;
BEGIN
    IF to_regclass('admin.files') IS NULL THEN
        RAISE EXCEPTION 'Required referenced table admin.files is missing';
    END IF;

    SELECT string_agg(
               format('%s expected %s not_null=%s, found %s not_null=%s',
                      expected.column_name,
                      expected.formatted_type,
                      expected.is_not_null,
                      COALESCE(actual.formatted_type, '<missing>'),
                      COALESCE(actual.is_not_null::text, '<missing>')),
               '; ' ORDER BY expected.column_name)
    INTO incompatible_columns
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
    WHERE actual.column_name IS NULL
       OR actual.formatted_type <> expected.formatted_type
       OR actual.is_not_null <> expected.is_not_null;

    IF incompatible_columns IS NOT NULL THEN
        RAISE EXCEPTION 'admin.files compatibility check failed: %', incompatible_columns;
    END IF;

    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint constraint_row
        JOIN pg_class source_table ON source_table.oid = constraint_row.conrelid
        JOIN pg_namespace source_schema ON source_schema.oid = source_table.relnamespace
        JOIN pg_class target_table ON target_table.oid = constraint_row.confrelid
        JOIN pg_namespace target_schema ON target_schema.oid = target_table.relnamespace
        WHERE source_schema.nspname = 'admin'
          AND source_table.relname = 'files'
          AND target_schema.nspname = 'admin'
          AND target_table.relname = 'users'
          AND constraint_row.contype = 'f'
          AND pg_get_constraintdef(constraint_row.oid, true)
              = 'FOREIGN KEY (insert_user) REFERENCES admin.users(id)') THEN
        RAISE EXCEPTION 'admin.files.insert_user must reference admin.users(id)';
    END IF;
END
$compatibility$;

DO $identity_bridge$
BEGIN
    IF EXISTS (
        SELECT 1
        FROM digital.attachments attachment
        WHERE attachment.state = 'Ready'
          AND attachment.message_id IS NOT NULL
          AND attachment.ready_key IS NOT NULL
          AND length(attachment.ready_key) > 50) THEN
        RAISE EXCEPTION
            'A bound Ready attachment ready_key exceeds admin.files.file_name varchar(50)';
    END IF;

    IF EXISTS (
        SELECT 1
        FROM digital.attachments attachment
        LEFT JOIN internal.support_users support_user
          ON support_user.id = attachment.client_user_id
         AND support_user.client_id::bigint = attachment.client_id
        LEFT JOIN admin.users mirrored_admin
          ON mirrored_admin.id = support_user.id
         AND mirrored_admin.user_name = support_user.user_name
        LEFT JOIN admin.users attachment_admin
          ON attachment_admin.id = attachment.admin_user_id
        WHERE attachment.state = 'Ready'
          AND attachment.message_id IS NOT NULL
          AND attachment.ready_key IS NOT NULL
          AND ((attachment.client_user_id IS NOT NULL AND mirrored_admin.id IS NULL)
            OR (attachment.admin_user_id IS NOT NULL AND attachment_admin.id IS NULL))) THEN
        RAISE EXCEPTION
            'A bound Ready attachment has no safe admin.files insert_user identity bridge';
    END IF;
END
$identity_bridge$;

DO $conflicts$
BEGIN
    IF EXISTS (
        SELECT 1
        FROM digital.attachments attachment
        JOIN admin.files company_file ON company_file.id = attachment.id::text
        WHERE attachment.state = 'Ready'
          AND attachment.message_id IS NOT NULL
          AND attachment.ready_key IS NOT NULL
          AND NOT (
              company_file.table_name = 'digital.instructions'
              AND company_file.table_id = attachment.message_id::text
              AND company_file.file_name = attachment.ready_key
              AND company_file.old_file_name IS NOT DISTINCT FROM attachment.display_name
              AND company_file.file_desc IS NOT DISTINCT FROM 'MESSAGE_ATTACHMENT'
              AND company_file.status IS TRUE
              AND company_file.edited_file IS NULL
              AND company_file.remarks IS NULL
              AND company_file.insert_user = COALESCE(
                    attachment.admin_user_id,
                    attachment.client_user_id))) THEN
        RAISE EXCEPTION
            'Conflicting admin.files metadata exists for a CBS Support attachment';
    END IF;
END
$conflicts$;

INSERT INTO admin.files (
    id, table_name, table_id, file_name, old_file_name,
    file_desc, status, edited_file, remarks,
    insert_user, insert_date, edit_user, edit_date)
SELECT attachment.id::text,
       'digital.instructions',
       attachment.message_id::text,
       attachment.ready_key,
       attachment.display_name,
       'MESSAGE_ATTACHMENT',
       TRUE,
       NULL,
       NULL,
       COALESCE(attachment.admin_user_id, attachment.client_user_id),
       COALESCE(attachment.bound_at, attachment.ready_at, attachment.created_at),
       NULL,
       NULL
FROM digital.attachments attachment
WHERE attachment.state = 'Ready'
  AND attachment.message_id IS NOT NULL
  AND attachment.ready_key IS NOT NULL
ON CONFLICT (id) DO NOTHING;
