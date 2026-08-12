-- Read-only verification for the shared test deployment; target: test.
-- Verifies the consolidated schema with catalog and invariant reads only.
-- Creates no temporary objects and does not read or write digital.schema_migrations.

BEGIN;

SET TRANSACTION READ ONLY;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '5min';
SET LOCAL idle_in_transaction_session_timeout = '2min';

DO $database_guard$
BEGIN
    IF current_database() <> 'test' THEN
        RAISE EXCEPTION
            'Refusing CBS Support verification: expected database test, connected to %',
            current_database();
    END IF;

    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'shovan') THEN
        RAISE EXCEPTION 'Required application role shovan does not exist';
    END IF;

    IF to_regclass('digital.schema_migrations') IS NOT NULL THEN
        RAISE EXCEPTION
            'Unexpected digital.schema_migrations exists; this manual deployment must not create a migration ledger';
    END IF;
END
$database_guard$;

DO $table_and_column_shape$
DECLARE
    failure text;
BEGIN
    SELECT expected.qualified_name
    INTO failure
    FROM unnest(ARRAY[
        'digital.instructions',
        'digital.conversation_access',
        'digital.conversation_sequences',
        'digital.conversation_read_cursors',
        'digital.conversation_outbox',
        'digital.conversation_audit',
        'digital.attachment_tenant_quotas',
        'digital.attachments',
        'digital.attachment_audit',
        'admin.users',
        'internal.support_users',
        'internal.clients'
    ]) AS expected(qualified_name)
    LEFT JOIN pg_class table_class
      ON table_class.oid = to_regclass(expected.qualified_name)
     AND table_class.relkind IN ('r','p')
    WHERE table_class.oid IS NULL
    LIMIT 1;

    IF failure IS NOT NULL THEN
        RAISE EXCEPTION 'Missing required table %', failure;
    END IF;

    IF EXISTS (
        WITH expected(table_schema, table_name, column_name, formatted_type) AS (
            VALUES
                ('internal','clients','id','integer'),
                ('internal','support_users','id','integer'),
                ('internal','support_users','client_id','integer')
        )
        SELECT 1
        FROM expected
        LEFT JOIN pg_namespace schema_row
          ON schema_row.nspname = expected.table_schema
        LEFT JOIN pg_class table_row
          ON table_row.relnamespace = schema_row.oid
         AND table_row.relname = expected.table_name
         AND table_row.relkind IN ('r','p')
        LEFT JOIN pg_attribute column_row
          ON column_row.attrelid = table_row.oid
         AND column_row.attname = expected.column_name
         AND column_row.attnum > 0
         AND NOT column_row.attisdropped
        WHERE column_row.attname IS NULL
           OR format_type(column_row.atttypid, column_row.atttypmod)
              <> expected.formatted_type
    ) THEN
        RAISE EXCEPTION
            'internal.clients.id and internal.support_users.id/client_id must match the confirmed integer live schema';
    END IF;

    IF EXISTS (
        SELECT 1
        FROM pg_attribute
        WHERE attrelid = 'internal.support_users'::regclass
          AND attname = 'user_id'
          AND attnum > 0
          AND NOT attisdropped
    ) THEN
        RAISE EXCEPTION
            'Unexpected internal.support_users.user_id exists; id is the inspected canonical support-login identity';
    END IF;

    IF NOT EXISTS (
        SELECT 1
        FROM pg_attribute
        WHERE attrelid = 'internal.support_users'::regclass
          AND attname = 'user_name'
          AND attnum > 0
          AND NOT attisdropped
    ) THEN
        RAISE EXCEPTION
            'internal.support_users.user_name is required by the client_id + user_name authentication lookup';
    END IF;

    IF NOT EXISTS (
        SELECT 1
        FROM pg_attribute column_row
        WHERE column_row.attrelid = 'internal.clients'::regclass
          AND column_row.attname = 'id'
          AND column_row.attnum > 0
          AND NOT column_row.attisdropped
          AND column_row.attnotnull
    ) OR NOT EXISTS (
        SELECT 1
        FROM pg_constraint key_constraint
        WHERE key_constraint.conrelid = 'internal.support_users'::regclass
          AND key_constraint.contype IN ('p','u')
          AND key_constraint.conkey = ARRAY[(
              SELECT attnum FROM pg_attribute
              WHERE attrelid = 'internal.support_users'::regclass
                AND attname = 'id' AND attnum > 0 AND NOT attisdropped
          )]::smallint[]
    ) OR NOT EXISTS (
        SELECT 1
        FROM pg_constraint key_constraint
        WHERE key_constraint.conrelid = 'internal.clients'::regclass
          AND key_constraint.contype IN ('p','u')
          AND key_constraint.conkey = ARRAY[(
              SELECT attnum FROM pg_attribute
              WHERE attrelid = 'internal.clients'::regclass
                AND attname = 'id' AND attnum > 0 AND NOT attisdropped
          )]::smallint[]
    ) THEN
        RAISE EXCEPTION
            'internal.support_users.id and the NOT NULL integer internal.clients.id must be usable single-column keys';
    END IF;

    WITH expected(table_name, column_name, formatted_type, is_not_null, identity_kind) AS (
        VALUES
            ('instructions','client_message_id','uuid',false,''),
            ('instructions','conversation_sequence','bigint',false,''),
            ('instructions','client_auth_user_id','integer',false,''),
            ('instructions','insert_user','integer',false,''),

            ('conversation_access','conversation_id','bigint',true,''),
            ('conversation_access','client_id','bigint',true,''),
            ('conversation_access','conversation_kind','character varying(16)',true,''),
            ('conversation_access','state','character varying(16)',true,''),
            ('conversation_access','client_user_id','integer',false,''),
            ('conversation_access','admin_user_id','integer',false,''),
            ('conversation_access','version','bigint',true,''),
            ('conversation_access','created_at','timestamp with time zone',true,''),
            ('conversation_access','archived_at','timestamp with time zone',false,''),

            ('conversation_sequences','conversation_id','bigint',true,''),
            ('conversation_sequences','next_sequence','bigint',true,''),

            ('conversation_read_cursors','read_cursor_id','bigint',true,'d'),
            ('conversation_read_cursors','conversation_id','bigint',true,''),
            ('conversation_read_cursors','principal_kind','character varying(16)',true,''),
            ('conversation_read_cursors','admin_user_id','integer',false,''),
            ('conversation_read_cursors','client_user_id','integer',false,''),
            ('conversation_read_cursors','last_read_sequence','bigint',true,''),
            ('conversation_read_cursors','updated_at','timestamp with time zone',true,''),

            ('conversation_outbox','event_id','uuid',true,''),
            ('conversation_outbox','conversation_id','bigint',true,''),
            ('conversation_outbox','client_id','bigint',true,''),
            ('conversation_outbox','conversation_kind','character varying(16)',true,''),
            ('conversation_outbox','conversation_state','character varying(16)',true,''),
            ('conversation_outbox','client_user_id','integer',false,''),
            ('conversation_outbox','admin_user_id','integer',false,''),
            ('conversation_outbox','access_version','bigint',true,''),
            ('conversation_outbox','message_id','bigint',false,''),
            ('conversation_outbox','event_type','character varying(64)',true,''),
            ('conversation_outbox','schema_version','smallint',true,''),
            ('conversation_outbox','payload','jsonb',true,''),
            ('conversation_outbox','occurred_at','timestamp with time zone',true,''),
            ('conversation_outbox','available_at','timestamp with time zone',true,''),
            ('conversation_outbox','attempt_count','integer',true,''),
            ('conversation_outbox','lease_owner','character varying(128)',false,''),
            ('conversation_outbox','lease_until','timestamp with time zone',false,''),
            ('conversation_outbox','processed_at','timestamp with time zone',false,''),
            ('conversation_outbox','dead_lettered_at','timestamp with time zone',false,''),
            ('conversation_outbox','last_error_code','character varying(64)',false,''),

            ('conversation_audit','audit_id','bigint',true,'d'),
            ('conversation_audit','conversation_id','bigint',true,''),
            ('conversation_audit','client_id','bigint',true,''),
            ('conversation_audit','action','character varying(64)',true,''),
            ('conversation_audit','actor_kind','character varying(16)',true,''),
            ('conversation_audit','admin_user_id','integer',false,''),
            ('conversation_audit','client_user_id','integer',false,''),
            ('conversation_audit','occurred_at','timestamp with time zone',true,''),
            ('conversation_audit','details','jsonb',true,''),

            ('attachment_tenant_quotas','client_id','bigint',true,''),
            ('attachment_tenant_quotas','active_storage_limit_bytes','bigint',true,''),
            ('attachment_tenant_quotas','updated_at','timestamp with time zone',true,''),

            ('attachments','id','uuid',true,''),
            ('attachments','client_id','bigint',true,''),
            ('attachments','conversation_id','bigint',true,''),
            ('attachments','message_id','bigint',false,''),
            ('attachments','position','smallint',false,''),
            ('attachments','admin_user_id','integer',false,''),
            ('attachments','client_user_id','integer',false,''),
            ('attachments','state','character varying(32)',true,''),
            ('attachments','quarantine_key','character varying(512)',false,''),
            ('attachments','ready_key','character varying(512)',false,''),
            ('attachments','display_name','character varying(255)',true,''),
            ('attachments','declared_media_type','character varying(128)',true,''),
            ('attachments','detected_media_type','character varying(128)',false,''),
            ('attachments','declared_size','bigint',true,''),
            ('attachments','actual_size','bigint',false,''),
            ('attachments','reservation_bytes','bigint',true,''),
            ('attachments','source_etag','character varying(256)',false,''),
            ('attachments','expected_ready_etag','character varying(256)',false,''),
            ('attachments','sha256','bytea',false,''),
            ('attachments','created_at','timestamp with time zone',true,''),
            ('attachments','updated_at','timestamp with time zone',true,''),
            ('attachments','upload_completed_at','timestamp with time zone',false,''),
            ('attachments','ready_at','timestamp with time zone',false,''),
            ('attachments','bound_at','timestamp with time zone',false,''),
            ('attachments','expires_at','timestamp with time zone',false,''),
            ('attachments','deleted_at','timestamp with time zone',false,''),
            ('attachments','lease_owner','character varying(128)',false,''),
            ('attachments','lease_until','timestamp with time zone',false,''),
            ('attachments','attempt_count','integer',true,''),
            ('attachments','next_attempt_at','timestamp with time zone',true,''),
            ('attachments','rejection_code','character varying(64)',false,''),
            ('attachments','delete_target_state','character varying(32)',false,''),
            ('attachments','last_error_code','character varying(64)',false,''),
            ('attachments','deletion_attempt_count','integer',true,''),

            ('attachment_audit','audit_id','bigint',true,'d'),
            ('attachment_audit','attachment_id','uuid',true,''),
            ('attachment_audit','client_id','bigint',true,''),
            ('attachment_audit','action','character varying(64)',true,''),
            ('attachment_audit','actor_kind','character varying(16)',true,''),
            ('attachment_audit','admin_user_id','integer',false,''),
            ('attachment_audit','client_user_id','integer',false,''),
            ('attachment_audit','occurred_at','timestamp with time zone',true,''),
            ('attachment_audit','details','jsonb',true,'')
    )
    SELECT format(
        'digital.%s.%s expected type=%s not_null=%s identity=%s; actual type=%s not_null=%s identity=%s',
        expected.table_name,
        expected.column_name,
        expected.formatted_type,
        expected.is_not_null,
        NULLIF(expected.identity_kind, ''),
        COALESCE(format_type(attribute.atttypid, attribute.atttypmod), '<missing>'),
        attribute.attnotnull,
        NULLIF(attribute.attidentity, '')
    )
    INTO failure
    FROM expected
    LEFT JOIN pg_class table_class
      ON table_class.oid = to_regclass(format('digital.%I', expected.table_name))
    LEFT JOIN pg_attribute attribute
      ON attribute.attrelid = table_class.oid
     AND attribute.attname = expected.column_name
     AND attribute.attnum > 0
     AND NOT attribute.attisdropped
    WHERE attribute.attname IS NULL
       OR format_type(attribute.atttypid, attribute.atttypmod) <> expected.formatted_type
       OR attribute.attnotnull <> expected.is_not_null
       OR attribute.attidentity <> expected.identity_kind
    LIMIT 1;

    IF failure IS NOT NULL THEN
        RAISE EXCEPTION 'Column verification failed: %', failure;
    END IF;

    -- Repository identity/default behavior must be present.
    IF pg_get_serial_sequence('digital.conversation_read_cursors', 'read_cursor_id') IS NULL
       OR pg_get_serial_sequence('digital.conversation_audit', 'audit_id') IS NULL
       OR pg_get_serial_sequence('digital.attachment_audit', 'audit_id') IS NULL THEN
        RAISE EXCEPTION 'One or more required BY DEFAULT identity sequences are missing';
    END IF;

    WITH expected(table_name, column_name, expected_default) AS (
        VALUES
            ('conversation_access','version','1'),
            ('conversation_access','created_at','now()'),
            ('conversation_read_cursors','last_read_sequence','0'),
            ('conversation_read_cursors','updated_at','now()'),
            ('conversation_outbox','schema_version','1'),
            ('conversation_outbox','occurred_at','now()'),
            ('conversation_outbox','available_at','now()'),
            ('conversation_outbox','attempt_count','0'),
            ('conversation_audit','occurred_at','now()'),
            ('conversation_audit','details','''{}''::jsonb'),
            ('attachment_tenant_quotas','updated_at','now()'),
            ('attachments','created_at','now()'),
            ('attachments','updated_at','now()'),
            ('attachments','attempt_count','0'),
            ('attachments','next_attempt_at','now()'),
            ('attachments','deletion_attempt_count','0'),
            ('attachment_audit','occurred_at','now()'),
            ('attachment_audit','details','''{}''::jsonb')
    )
    SELECT format(
        'digital.%s.%s expected default %s, actual %s',
        expected.table_name,
        expected.column_name,
        expected.expected_default,
        COALESCE(pg_get_expr(column_default.adbin, column_default.adrelid), '<missing>')
    )
    INTO failure
    FROM expected
    JOIN pg_class table_class
      ON table_class.oid = to_regclass(format('digital.%I', expected.table_name))
    JOIN pg_attribute attribute
      ON attribute.attrelid = table_class.oid
     AND attribute.attname = expected.column_name
     AND attribute.attnum > 0
     AND NOT attribute.attisdropped
    LEFT JOIN pg_attrdef column_default
      ON column_default.adrelid = attribute.attrelid
     AND column_default.adnum = attribute.attnum
    WHERE regexp_replace(
              COALESCE(pg_get_expr(column_default.adbin, column_default.adrelid), ''),
              '[[:space:]()]', '', 'g')
          <> regexp_replace(expected.expected_default, '[[:space:]()]', '', 'g')
    LIMIT 1;

    IF failure IS NOT NULL THEN
        RAISE EXCEPTION 'Default verification failed: %', failure;
    END IF;
END
$table_and_column_shape$;

DO $instruction_client_identity_fk$
BEGIN
    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint constraint_row
        WHERE constraint_row.conrelid = 'digital.instructions'::regclass
          AND constraint_row.conname = 'fk_instructions_client_auth_support_user'
          AND constraint_row.contype = 'f'
          AND constraint_row.convalidated
          AND constraint_row.conkey = ARRAY[(
              SELECT column_row.attnum
              FROM pg_attribute column_row
              WHERE column_row.attrelid = 'digital.instructions'::regclass
                AND column_row.attname = 'client_auth_user_id'
                AND column_row.attnum > 0
                AND NOT column_row.attisdropped
          )]::smallint[]
          AND constraint_row.confrelid = 'internal.support_users'::regclass
          AND constraint_row.confkey = ARRAY[(
              SELECT column_row.attnum
              FROM pg_attribute column_row
              WHERE column_row.attrelid = 'internal.support_users'::regclass
                AND column_row.attname = 'id'
                AND column_row.attnum > 0
                AND NOT column_row.attisdropped
          )]::smallint[]
    ) THEN
        RAISE EXCEPTION
            'Instruction client identity FK must have the exact name, validated state, source column, and internal.support_users(id) target';
    END IF;
END
$instruction_client_identity_fk$;

DO $key_constraints$
DECLARE
    failure text;
BEGIN
    WITH expected(
        table_name, constraint_name, constraint_type,
        key_columns, referenced_table, referenced_columns
    ) AS (
        VALUES
            ('instructions','fk_instructions_client_auth_support_user','f','client_auth_user_id','internal.support_users','id'),
            ('instructions','uq_instructions_id_conversation_client','u','id,instruction_id,client_id',NULL,NULL),
            ('conversation_access','pk_conversation_access','p','conversation_id',NULL,NULL),
            ('conversation_access','fk_conversation_access_conversation','f','conversation_id','digital.instructions','id'),
            ('conversation_access','fk_conversation_access_client_user','f','client_user_id','internal.support_users','id'),
            ('conversation_access','fk_conversation_access_admin_user','f','admin_user_id','admin.users','id'),
            ('conversation_access','uq_conversation_access_id_client','u','conversation_id,client_id',NULL,NULL),
            ('conversation_sequences','pk_conversation_sequences','p','conversation_id',NULL,NULL),
            ('conversation_sequences','fk_conversation_sequences_conversation','f','conversation_id','digital.instructions','id'),
            ('conversation_read_cursors','pk_conversation_read_cursors','p','read_cursor_id',NULL,NULL),
            ('conversation_read_cursors','fk_conversation_read_cursors_access','f','conversation_id','digital.conversation_access','conversation_id'),
            ('conversation_read_cursors','fk_conversation_read_cursors_admin_user','f','admin_user_id','admin.users','id'),
            ('conversation_read_cursors','fk_conversation_read_cursors_client_user','f','client_user_id','internal.support_users','id'),
            ('conversation_outbox','pk_conversation_outbox','p','event_id',NULL,NULL),
            ('conversation_outbox','fk_conversation_outbox_access','f','conversation_id','digital.conversation_access','conversation_id'),
            ('conversation_outbox','fk_conversation_outbox_client_user','f','client_user_id','internal.support_users','id'),
            ('conversation_outbox','fk_conversation_outbox_admin_user','f','admin_user_id','admin.users','id'),
            ('conversation_outbox','fk_conversation_outbox_message','f','message_id','digital.instructions','id'),
            ('conversation_audit','pk_conversation_audit','p','audit_id',NULL,NULL),
            ('conversation_audit','fk_conversation_audit_access','f','conversation_id','digital.conversation_access','conversation_id'),
            ('conversation_audit','fk_conversation_audit_admin_user','f','admin_user_id','admin.users','id'),
            ('conversation_audit','fk_conversation_audit_client_user','f','client_user_id','internal.support_users','id'),
            ('attachment_tenant_quotas','pk_attachment_tenant_quotas','p','client_id',NULL,NULL),
            ('attachments','pk_attachments','p','id',NULL,NULL),
            ('attachments','fk_attachments_conversation','f','conversation_id','digital.conversation_access','conversation_id'),
            ('attachments','fk_attachments_message','f','message_id','digital.instructions','id'),
            ('attachments','fk_attachments_admin_user','f','admin_user_id','admin.users','id'),
            ('attachments','fk_attachments_client_user','f','client_user_id','internal.support_users','id'),
            ('attachments','uq_attachments_id_client','u','id,client_id',NULL,NULL),
            ('attachments','fk_attachments_conversation_client','f','conversation_id,client_id','digital.conversation_access','conversation_id,client_id'),
            ('attachments','fk_attachments_message_conversation_client','f','message_id,conversation_id,client_id','digital.instructions','id,instruction_id,client_id'),
            ('attachment_audit','pk_attachment_audit','p','audit_id',NULL,NULL),
            ('attachment_audit','fk_attachment_audit_attachment','f','attachment_id','digital.attachments','id'),
            ('attachment_audit','fk_attachment_audit_admin_user','f','admin_user_id','admin.users','id'),
            ('attachment_audit','fk_attachment_audit_client_user','f','client_user_id','internal.support_users','id'),
            ('attachment_audit','fk_attachment_audit_attachment_client','f','attachment_id,client_id','digital.attachments','id,client_id')
    ), actual AS (
        SELECT constraint_row.*,
               string_agg(key_attribute.attname, ',' ORDER BY key_position.ordinality) AS key_columns,
               referenced_table.oid::regclass::text AS referenced_table,
               string_agg(referenced_attribute.attname, ',' ORDER BY key_position.ordinality)
                   FILTER (WHERE constraint_row.contype = 'f') AS referenced_columns
        FROM pg_constraint constraint_row
        JOIN pg_class constrained_table ON constrained_table.oid = constraint_row.conrelid
        JOIN pg_namespace constrained_schema ON constrained_schema.oid = constrained_table.relnamespace
        LEFT JOIN LATERAL unnest(constraint_row.conkey)
            WITH ORDINALITY AS key_position(attnum, ordinality) ON true
        LEFT JOIN pg_attribute key_attribute
          ON key_attribute.attrelid = constrained_table.oid
         AND key_attribute.attnum = key_position.attnum
        LEFT JOIN pg_class referenced_table ON referenced_table.oid = constraint_row.confrelid
        LEFT JOIN pg_attribute referenced_attribute
          ON referenced_attribute.attrelid = referenced_table.oid
         AND referenced_attribute.attnum = constraint_row.confkey[key_position.ordinality]
        WHERE constrained_schema.nspname = 'digital'
        GROUP BY constraint_row.oid, referenced_table.oid
    )
    SELECT format(
        'digital.%s constraint %s expected type=%s keys=(%s) reference=%s(%s)',
        expected.table_name,
        expected.constraint_name,
        expected.constraint_type,
        expected.key_columns,
        expected.referenced_table,
        expected.referenced_columns
    )
    INTO failure
    FROM expected
    LEFT JOIN actual
      ON actual.conrelid = to_regclass(format('digital.%I', expected.table_name))
     AND actual.conname = expected.constraint_name
    WHERE actual.oid IS NULL
       OR actual.contype::text <> expected.constraint_type
       OR actual.key_columns IS DISTINCT FROM expected.key_columns
       OR actual.referenced_table IS DISTINCT FROM expected.referenced_table
       OR actual.referenced_columns IS DISTINCT FROM expected.referenced_columns
       OR NOT actual.convalidated
       OR actual.condeferrable
       OR actual.condeferred
       OR (actual.contype = 'f' AND (
              actual.confupdtype <> 'a'
              OR actual.confdeltype <> 'a'
              OR actual.confmatchtype <> 's'))
    LIMIT 1;

    IF failure IS NOT NULL THEN
        RAISE EXCEPTION 'Key constraint verification failed: %', failure;
    END IF;

    -- The legacy table must expose a single-column primary key for new foreign keys.
    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint constraint_row
        WHERE constraint_row.conrelid = 'digital.instructions'::regclass
          AND constraint_row.contype = 'p'
          AND constraint_row.conkey = ARRAY[(
              SELECT attnum
              FROM pg_attribute
              WHERE attrelid = 'digital.instructions'::regclass
                AND attname = 'id'
                AND attnum > 0
                AND NOT attisdropped
          )]::smallint[]
          AND constraint_row.convalidated
          AND NOT constraint_row.condeferrable
    ) THEN
        RAISE EXCEPTION 'digital.instructions must have a nondeferrable primary key on id';
    END IF;
END
$key_constraints$;

DO $check_constraints$
DECLARE
    failure text;
    definition text;
BEGIN
    WITH expected(table_name, constraint_name) AS (
        VALUES
            ('instructions','ck_instructions_client_author_exclusive'),
            ('instructions','ck_instructions_conversation_sequence_shape'),
            ('conversation_access','ck_conversation_access_kind'),
            ('conversation_access','ck_conversation_access_state'),
            ('conversation_access','ck_conversation_access_version_positive'),
            ('conversation_access','ck_conversation_access_archived_timestamp'),
            ('conversation_access','ck_conversation_access_participants'),
            ('conversation_sequences','ck_conversation_sequences_next_positive'),
            ('conversation_read_cursors','ck_conversation_read_cursors_principal_kind'),
            ('conversation_read_cursors','ck_conversation_read_cursors_principal'),
            ('conversation_read_cursors','ck_conversation_read_cursors_sequence_nonnegative'),
            ('conversation_outbox','ck_conversation_outbox_event_type_nonempty'),
            ('conversation_outbox','ck_conversation_outbox_kind'),
            ('conversation_outbox','ck_conversation_outbox_state'),
            ('conversation_outbox','ck_conversation_outbox_participants'),
            ('conversation_outbox','ck_conversation_outbox_access_version_positive'),
            ('conversation_outbox','ck_conversation_outbox_schema_version_positive'),
            ('conversation_outbox','ck_conversation_outbox_payload_object'),
            ('conversation_outbox','ck_conversation_outbox_availability_order'),
            ('conversation_outbox','ck_conversation_outbox_attempt_nonnegative'),
            ('conversation_outbox','ck_conversation_outbox_lease_pair'),
            ('conversation_outbox','ck_conversation_outbox_lease_owner_nonempty'),
            ('conversation_outbox','ck_conversation_outbox_terminal_exclusive'),
            ('conversation_outbox','ck_conversation_outbox_terminal_not_leased'),
            ('conversation_outbox','ck_conversation_outbox_error_code_nonempty'),
            ('conversation_audit','ck_conversation_audit_action_nonempty'),
            ('conversation_audit','ck_conversation_audit_actor_kind'),
            ('conversation_audit','ck_conversation_audit_actor'),
            ('conversation_audit','ck_conversation_audit_details_object'),
            ('attachment_tenant_quotas','ck_attachment_tenant_quota_minimum'),
            ('attachments','ck_attachments_state'),
            ('attachments','ck_attachments_uploader'),
            ('attachments','ck_attachments_sizes'),
            ('attachments','ck_attachments_position'),
            ('attachments','ck_attachments_lease_pair'),
            ('attachments','ck_attachments_attempt_nonnegative'),
            ('attachments','ck_attachments_rejection_code'),
            ('attachments','ck_attachments_delete_target'),
            ('attachments','ck_attachments_ready_shape'),
            ('attachments','ck_attachments_terminal_reservation'),
            ('attachments','ck_attachments_last_error_code'),
            ('attachments','ck_attachments_deletion_attempt_nonnegative'),
            ('attachments','ck_attachments_bound_retention'),
            ('attachments','ck_attachments_ready_unbound_retention'),
            ('attachment_audit','ck_attachment_audit_action'),
            ('attachment_audit','ck_attachment_audit_actor_kind'),
            ('attachment_audit','ck_attachment_audit_actor'),
            ('attachment_audit','ck_attachment_audit_details')
    )
    SELECT format('digital.%s constraint %s', expected.table_name, expected.constraint_name)
    INTO failure
    FROM expected
    LEFT JOIN pg_constraint actual
      ON actual.conrelid = to_regclass(format('digital.%I', expected.table_name))
     AND actual.conname = expected.constraint_name
     AND actual.contype = 'c'
    WHERE actual.oid IS NULL
       OR NOT actual.convalidated
       OR actual.connoinherit
    LIMIT 1;

    IF failure IS NOT NULL THEN
        RAISE EXCEPTION 'Missing, invalid, or inherited check constraint: %', failure;
    END IF;

    -- Verify the StructuralValidationOnly state set and shape.
    SELECT lower(pg_get_constraintdef(oid, true))
    INTO definition
    FROM pg_constraint
    WHERE conrelid = 'digital.attachments'::regclass
      AND conname = 'ck_attachments_state';

    IF definition IS NULL
       OR definition NOT LIKE '%''pendingupload''%'
       OR definition NOT LIKE '%''uploaded''%'
       OR definition NOT LIKE '%''structuralvalidation''%'
       OR definition NOT LIKE '%''structurallyvalidated''%'
       OR definition NOT LIKE '%''scanning''%'
       OR definition NOT LIKE '%''promoting''%'
       OR definition NOT LIKE '%''ready''%'
       OR definition NOT LIKE '%''rejected''%'
       OR definition NOT LIKE '%''scanfailed''%'
       OR definition NOT LIKE '%''deletepending''%'
       OR definition NOT LIKE '%''deleted''%'
       OR definition NOT LIKE '%''expired''%' THEN
        RAISE EXCEPTION 'ck_attachments_state is not the final StructuralValidationOnly definition';
    END IF;

    SELECT lower(pg_get_constraintdef(oid, true))
    INTO definition
    FROM pg_constraint
    WHERE conrelid = 'digital.attachments'::regclass
      AND conname = 'ck_attachments_ready_shape';

    IF definition IS NULL
       OR definition NOT LIKE '%''structurallyvalidated''%'
       OR definition NOT LIKE '%''ready''%'
       OR definition NOT LIKE '%''promoting''%'
       OR definition NOT LIKE '%ready_key is not null%'
       OR definition NOT LIKE '%source_etag is not null%'
       OR definition NOT LIKE '%sha256 is not null%'
       OR definition NOT LIKE '%actual_size is not null%'
       OR definition NOT LIKE '%detected_media_type is not null%' THEN
        RAISE EXCEPTION 'ck_attachments_ready_shape is not the final StructuralValidationOnly definition';
    END IF;

    SELECT lower(pg_get_constraintdef(oid, true))
    INTO definition
    FROM pg_constraint
    WHERE conrelid = 'digital.attachments'::regclass
      AND conname = 'ck_attachments_sizes';

    IF definition IS NULL
       OR definition NOT LIKE '%declared_size%10485760%'
       OR definition NOT LIKE '%actual_size%0%'
       OR definition NOT LIKE '%reservation_bytes%0%'
       OR definition NOT LIKE '%''uploaded''%'
       OR definition NOT LIKE '%''structuralvalidation''%'
       OR definition NOT LIKE '%''structurallyvalidated''%'
       OR definition NOT LIKE '%greatest%declared_size%actual_size%' THEN
        RAISE EXCEPTION 'ck_attachments_sizes is not the final StructuralValidationOnly definition';
    END IF;

    -- Guard Messaging V2 and attachment forward fixes by definition, not names alone.
    SELECT lower(pg_get_constraintdef(oid, true)) INTO definition
    FROM pg_constraint
    WHERE conrelid = 'digital.conversation_access'::regclass
      AND conname = 'ck_conversation_access_kind';
    IF definition NOT LIKE '%''group''%'
       OR definition NOT LIKE '%''private''%'
       OR definition NOT LIKE '%''ticket''%'
       OR definition NOT LIKE '%''inquiry''%' THEN
        RAISE EXCEPTION 'ck_conversation_access_kind lacks a final conversation kind';
    END IF;

    SELECT lower(pg_get_constraintdef(oid, true)) INTO definition
    FROM pg_constraint
    WHERE conrelid = 'digital.instructions'::regclass
      AND conname = 'ck_instructions_conversation_sequence_shape';
    IF definition NOT LIKE '%inst_type_id = 105%'
       OR definition NOT LIKE '%conversation_sequence = 0%'
       OR definition NOT LIKE '%conversation_sequence = 1%'
       OR definition NOT LIKE '%conversation_sequence > 0%'
       OR definition NOT LIKE '%client_message_id is not null%'
       OR definition NOT LIKE '%btrim(instruction)%' THEN
        RAISE EXCEPTION 'ck_instructions_conversation_sequence_shape is not the final case-capable definition';
    END IF;

    SELECT lower(pg_get_constraintdef(oid, true)) INTO definition
    FROM pg_constraint
    WHERE conrelid = 'digital.attachments'::regclass
      AND conname = 'ck_attachments_bound_retention';
    IF (definition NOT LIKE '%365 days%' AND definition NOT LIKE '%8760:00:00%')
       OR definition NOT LIKE '%expires_at%'
       OR definition NOT LIKE '%bound_at%' THEN
        RAISE EXCEPTION 'ck_attachments_bound_retention definition is incompatible';
    END IF;

    SELECT lower(pg_get_constraintdef(oid, true)) INTO definition
    FROM pg_constraint
    WHERE conrelid = 'digital.attachments'::regclass
      AND conname = 'ck_attachments_ready_unbound_retention';
    IF (definition NOT LIKE '%24 hours%' AND definition NOT LIKE '%24:00:00%')
       OR definition NOT LIKE '%ready_at%'
       OR definition NOT LIKE '%expires_at%'
       OR definition NOT LIKE '%message_id is not null%' THEN
        RAISE EXCEPTION 'ck_attachments_ready_unbound_retention definition is incompatible';
    END IF;
END
$check_constraints$;

DO $indexes$
DECLARE
    failure text;
BEGIN
    WITH expected(
        table_name, index_name, is_unique, key_columns, sort_options, expected_predicate
    ) AS (
        VALUES
            ('instructions','ix_instructions_conversation_sequence_unique',true,'instruction_id,conversation_sequence','0,0','instruction_idisnotnull'),
            ('instructions','ix_instructions_client_message_unique',true,'client_message_id','0','client_message_idisnotnull'),
            ('conversation_access','ix_conversation_access_group_unique',true,'client_id','0','conversation_kind=''group'''),
            ('conversation_access','ix_conversation_access_active_private_pair_unique',true,'client_id,client_user_id,admin_user_id','0,0,0','conversation_kind=''private''andstate=''active'''),
            ('conversation_read_cursors','ix_conversation_read_cursors_admin_unique',true,'conversation_id,admin_user_id','0,0','principal_kind=''admin'''),
            ('conversation_read_cursors','ix_conversation_read_cursors_client_unique',true,'conversation_id,client_user_id','0,0','principal_kind=''client'''),
            ('conversation_access','ix_conversation_access_client_state_kind',false,'client_id,state,conversation_kind,conversation_id','0,0,0,0',NULL),
            ('conversation_outbox','ix_conversation_outbox_dispatch',false,'available_at,event_id','0,0','processed_atisnullanddead_lettered_atisnull'),
            ('conversation_audit','ix_conversation_audit_conversation_occurred',false,'conversation_id,occurred_at,audit_id','0,3,3',NULL),
            ('conversation_audit','ix_conversation_audit_client_occurred',false,'client_id,occurred_at,audit_id','0,3,3',NULL),
            ('attachments','uq_attachments_message_position',true,'message_id,position','0,0','message_idisnotnull'),
            ('attachments','uq_attachments_quarantine_key',true,'quarantine_key','0','quarantine_keyisnotnull'),
            ('attachments','uq_attachments_ready_key',true,'ready_key','0','ready_keyisnotnull'),
            ('attachments','ix_attachments_conversation_state',false,'conversation_id,state,created_at','0,0,0',NULL),
            ('attachments','ix_attachments_client_active_storage',false,'client_id,state','0,0','state=anyarray[''pendingupload'',''uploaded'',''structuralvalidation'',''structurallyvalidated'',''scanning'',''promoting'',''ready'',''deletepending'']'),
            ('attachments','ix_attachments_user_rolling_quota',false,'client_id,client_user_id,admin_user_id,created_at','0,0,0,3',NULL),
            ('attachments','ix_attachments_scan_claim',false,'next_attempt_at,created_at','0,0','state=anyarray[''uploaded'',''structuralvalidation'',''structurallyvalidated'',''scanning'',''promoting'']'),
            ('attachments','ix_attachments_cleanup_claim',false,'state,expires_at,created_at','0,0,0','state=anyarray[''pendingupload'',''ready'',''deletepending'']'),
            ('attachments','ix_attachments_ready_quarantine_cleanup',false,'next_attempt_at,ready_at,id','0,0,0','state=''ready''andquarantine_keyisnotnull'),
            ('attachment_audit','ix_attachment_audit_attachment_time',false,'attachment_id,occurred_at,audit_id','0,0,0',NULL)
    ), actual AS (
        SELECT index_class.oid,
               table_class.relname AS table_name,
               index_class.relname AS index_name,
               index_state.indisunique,
               index_state.indisvalid,
               index_state.indisready,
               index_state.indislive,
               index_state.indisprimary,
               access_method.amname,
               string_agg(attribute.attname, ',' ORDER BY key_position.ordinality)
                   FILTER (WHERE key_position.ordinality <= index_state.indnkeyatts) AS key_columns,
               string_agg(index_state.indoption[key_position.ordinality - 1]::text, ','
                   ORDER BY key_position.ordinality)
                   FILTER (WHERE key_position.ordinality <= index_state.indnkeyatts) AS sort_options,
               regexp_replace(
                   regexp_replace(
                       lower(COALESCE(pg_get_expr(index_state.indpred, index_state.indrelid), '')),
                       '::(text|character varying)(\[\])?', '', 'g'),
                   '[[:space:]()]', '', 'g') AS canonical_predicate
        FROM pg_class index_class
        JOIN pg_namespace index_schema ON index_schema.oid = index_class.relnamespace
        JOIN pg_index index_state ON index_state.indexrelid = index_class.oid
        JOIN pg_class table_class ON table_class.oid = index_state.indrelid
        JOIN pg_am access_method ON access_method.oid = index_class.relam
        LEFT JOIN LATERAL unnest(index_state.indkey)
            WITH ORDINALITY AS key_position(attnum, ordinality) ON true
        LEFT JOIN pg_attribute attribute
          ON attribute.attrelid = table_class.oid
         AND attribute.attnum = key_position.attnum
        WHERE index_schema.nspname = 'digital'
        GROUP BY index_class.oid, table_class.relname, index_state.indexrelid,
                 index_state.indrelid, access_method.amname
    )
    SELECT format(
        'digital.%s index %s expected unique=%s keys=(%s) sort=(%s) predicate=%s; actual predicate=%s',
        expected.table_name,
        expected.index_name,
        expected.is_unique,
        expected.key_columns,
        expected.sort_options,
        expected.expected_predicate,
        actual.canonical_predicate
    )
    INTO failure
    FROM expected
    LEFT JOIN actual
      ON actual.table_name = expected.table_name
     AND actual.index_name = expected.index_name
    WHERE actual.oid IS NULL
       OR actual.indisunique <> expected.is_unique
       OR actual.key_columns IS DISTINCT FROM expected.key_columns
       OR actual.sort_options IS DISTINCT FROM expected.sort_options
       OR NULLIF(actual.canonical_predicate, '') IS DISTINCT FROM expected.expected_predicate
       OR NOT actual.indisvalid
       OR NOT actual.indisready
       OR NOT actual.indislive
       OR actual.indisprimary
       OR actual.amname <> 'btree'
    LIMIT 1;

    IF failure IS NOT NULL THEN
        RAISE EXCEPTION 'Index verification failed: %', failure;
    END IF;
END
$indexes$;

DO $functions_and_triggers$
DECLARE
    actual_body text;
    expected_body text;
    function_oid oid;
    expected_update_columns smallint[];
BEGIN
    function_oid := to_regprocedure('digital.maintain_attachment_quota_reservation()');
    IF function_oid IS NULL THEN
        RAISE EXCEPTION 'Missing function digital.maintain_attachment_quota_reservation()';
    END IF;

    SELECT regexp_replace(btrim(procedure_row.prosrc), '[[:space:]]+', ' ', 'g')
    INTO actual_body
    FROM pg_proc procedure_row
    JOIN pg_language language_row ON language_row.oid = procedure_row.prolang
    WHERE procedure_row.oid = function_oid
      AND procedure_row.prorettype = 'trigger'::regtype
      AND procedure_row.pronargs = 0
      AND language_row.lanname = 'plpgsql'
      AND NOT procedure_row.prosecdef
      AND procedure_row.provolatile = 'v';

    expected_body := regexp_replace(btrim($body$
BEGIN
    IF NEW.state IN (
        'PendingUpload','Uploaded','StructuralValidation','StructurallyValidated',
        'Scanning','Promoting','Ready','DeletePending') THEN
        NEW.reservation_bytes := GREATEST(
            NEW.reservation_bytes,
            NEW.declared_size,
            COALESCE(NEW.actual_size, 0));
    END IF;
    RETURN NEW;
END
$body$), '[[:space:]]+', ' ', 'g');

    IF actual_body IS DISTINCT FROM expected_body THEN
        RAISE EXCEPTION 'digital.maintain_attachment_quota_reservation() body or attributes are incompatible';
    END IF;

    function_oid := to_regprocedure('digital.enforce_attachment_client_uploader_tenant()');
    IF function_oid IS NULL THEN
        RAISE EXCEPTION 'Missing function digital.enforce_attachment_client_uploader_tenant()';
    END IF;

    SELECT regexp_replace(btrim(procedure_row.prosrc), '[[:space:]]+', ' ', 'g')
    INTO actual_body
    FROM pg_proc procedure_row
    JOIN pg_language language_row ON language_row.oid = procedure_row.prolang
    WHERE procedure_row.oid = function_oid
      AND procedure_row.prorettype = 'trigger'::regtype
      AND procedure_row.pronargs = 0
      AND language_row.lanname = 'plpgsql'
      AND NOT procedure_row.prosecdef
      AND procedure_row.provolatile = 'v';

    expected_body := regexp_replace(btrim($body$
BEGIN
    IF NEW.client_user_id IS NOT NULL
       AND NOT EXISTS (
            SELECT 1
            FROM internal.support_users uploader
            WHERE uploader.id = NEW.client_user_id
              AND uploader.client_id = NEW.client_id
       ) THEN
        RAISE EXCEPTION 'Attachment client uploader tenant mismatch'
            USING ERRCODE = '23514';
    END IF;
    RETURN NEW;
END
$body$), '[[:space:]]+', ' ', 'g');

    IF actual_body IS DISTINCT FROM expected_body THEN
        RAISE EXCEPTION 'digital.enforce_attachment_client_uploader_tenant() body or attributes are incompatible';
    END IF;

    SELECT ARRAY[
        (SELECT attnum FROM pg_attribute WHERE attrelid = 'digital.attachments'::regclass AND attname = 'state'),
        (SELECT attnum FROM pg_attribute WHERE attrelid = 'digital.attachments'::regclass AND attname = 'declared_size'),
        (SELECT attnum FROM pg_attribute WHERE attrelid = 'digital.attachments'::regclass AND attname = 'actual_size'),
        (SELECT attnum FROM pg_attribute WHERE attrelid = 'digital.attachments'::regclass AND attname = 'reservation_bytes')
    ]::smallint[]
    INTO expected_update_columns;

    IF NOT EXISTS (
        SELECT 1
        FROM pg_trigger trigger_row
        WHERE trigger_row.tgrelid = 'digital.attachments'::regclass
          AND trigger_row.tgname = 'trg_attachments_quota_reservation'
          AND trigger_row.tgfoid = 'digital.maintain_attachment_quota_reservation()'::regprocedure
          AND trigger_row.tgtype = 23
          AND trigger_row.tgattr::text = array_to_string(expected_update_columns, ' ')
          AND trigger_row.tgenabled = 'O'
          AND NOT trigger_row.tgisinternal
          AND trigger_row.tgnargs = 0
    ) THEN
        RAISE EXCEPTION 'trg_attachments_quota_reservation definition is missing, disabled, or incompatible';
    END IF;

    SELECT ARRAY[
        (SELECT attnum FROM pg_attribute WHERE attrelid = 'digital.attachments'::regclass AND attname = 'client_user_id'),
        (SELECT attnum FROM pg_attribute WHERE attrelid = 'digital.attachments'::regclass AND attname = 'client_id')
    ]::smallint[]
    INTO expected_update_columns;

    IF NOT EXISTS (
        SELECT 1
        FROM pg_trigger trigger_row
        WHERE trigger_row.tgrelid = 'digital.attachments'::regclass
          AND trigger_row.tgname = 'trg_attachments_client_uploader_tenant'
          AND trigger_row.tgfoid = 'digital.enforce_attachment_client_uploader_tenant()'::regprocedure
          AND trigger_row.tgtype = 23
          AND trigger_row.tgattr::text = array_to_string(expected_update_columns, ' ')
          AND trigger_row.tgenabled = 'O'
          AND NOT trigger_row.tgisinternal
          AND trigger_row.tgnargs = 0
    ) THEN
        RAISE EXCEPTION 'trg_attachments_client_uploader_tenant definition is missing, disabled, or incompatible';
    END IF;
END
$functions_and_triggers$;

DO $application_permissions$
DECLARE
    failure text;
    instructions_id_sequence text;
BEGIN
    IF NOT has_schema_privilege('shovan', 'digital', 'USAGE')
       OR NOT has_schema_privilege('shovan', 'admin', 'USAGE')
       OR NOT has_schema_privilege('shovan', 'internal', 'USAGE') THEN
        RAISE EXCEPTION 'Role shovan requires USAGE on schemas digital, admin, and internal';
    END IF;

    WITH expected(qualified_name, required_privileges) AS (
        VALUES
            ('digital.instructions','SELECT, INSERT, UPDATE'),
            ('digital.conversation_access','SELECT, INSERT, UPDATE'),
            ('digital.conversation_sequences','SELECT, INSERT, UPDATE'),
            ('digital.conversation_read_cursors','SELECT, INSERT, UPDATE'),
            ('digital.conversation_outbox','SELECT, INSERT, UPDATE'),
            ('digital.conversation_audit','SELECT, INSERT'),
            ('digital.attachment_tenant_quotas','SELECT'),
            ('digital.attachments','SELECT, INSERT, UPDATE'),
            ('digital.attachment_audit','SELECT, INSERT'),
            ('admin.users','SELECT'),
            ('internal.support_users','SELECT'),
            ('internal.clients','SELECT')
    )
    SELECT format('%s on %s', expected.required_privileges, expected.qualified_name)
    INTO failure
    FROM expected
    WHERE NOT has_table_privilege(
        'shovan', expected.qualified_name, expected.required_privileges)
    LIMIT 1;

    IF failure IS NOT NULL THEN
        RAISE EXCEPTION 'Role shovan lacks required application privilege %', failure;
    END IF;

    WITH expected(qualified_name) AS (
        VALUES
            ('digital.conversation_read_cursors_read_cursor_id_seq'),
            ('digital.conversation_audit_audit_id_seq'),
            ('digital.attachment_audit_audit_id_seq')
    )
    SELECT expected.qualified_name
    INTO failure
    FROM expected
    WHERE to_regclass(expected.qualified_name) IS NULL
       OR NOT has_sequence_privilege('shovan', expected.qualified_name, 'USAGE')
    LIMIT 1;

    IF failure IS NOT NULL THEN
        RAISE EXCEPTION 'Role shovan lacks USAGE on required identity sequence %', failure;
    END IF;

    instructions_id_sequence := pg_get_serial_sequence('digital.instructions', 'id');
    IF instructions_id_sequence IS NULL THEN
        RAISE EXCEPTION 'digital.instructions.id has no serial/identity sequence';
    END IF;
    IF NOT has_sequence_privilege('shovan', instructions_id_sequence, 'USAGE') THEN
        RAISE EXCEPTION
            'Role shovan lacks USAGE on digital.instructions ID sequence %',
            instructions_id_sequence;
    END IF;

    IF NOT has_function_privilege(
           'shovan', 'digital.maintain_attachment_quota_reservation()', 'EXECUTE')
       OR NOT has_function_privilege(
           'shovan', 'digital.enforce_attachment_client_uploader_tenant()', 'EXECUTE') THEN
        RAISE EXCEPTION 'Role shovan lacks EXECUTE on an attachment trigger function';
    END IF;
END
$application_permissions$;

DO $data_invariants$
BEGIN
    IF EXISTS (
        SELECT 1
        FROM digital.instructions instruction
        LEFT JOIN internal.support_users support_user
          ON support_user.id = instruction.client_auth_user_id
        WHERE instruction.client_auth_user_id IS NOT NULL
          AND (
              support_user.id IS NULL
              OR support_user.client_id IS DISTINCT FROM instruction.client_id
              OR instruction.insert_user IS NOT NULL)
    ) THEN
        RAISE EXCEPTION 'Client-authored instruction principal/tenant invariant failed';
    END IF;

    IF EXISTS (
        SELECT 1
        FROM digital.conversation_access access
        LEFT JOIN internal.support_users support_user
          ON support_user.id = access.client_user_id
        WHERE access.client_user_id IS NOT NULL
          AND (support_user.id IS NULL
               OR support_user.client_id IS DISTINCT FROM access.client_id)
    ) THEN
        RAISE EXCEPTION 'Conversation client participant tenant invariant failed';
    END IF;

    IF EXISTS (
        SELECT 1
        FROM digital.conversation_read_cursors cursor_row
        JOIN digital.conversation_access access
          ON access.conversation_id = cursor_row.conversation_id
        LEFT JOIN internal.support_users support_user
          ON support_user.id = cursor_row.client_user_id
        WHERE cursor_row.client_user_id IS NOT NULL
          AND (support_user.id IS NULL
               OR support_user.client_id IS DISTINCT FROM access.client_id)
    ) THEN
        RAISE EXCEPTION 'Conversation read-cursor client principal tenant invariant failed';
    END IF;

    IF EXISTS (
        SELECT 1
        FROM digital.conversation_outbox outbox
        JOIN digital.conversation_access access
          ON access.conversation_id = outbox.conversation_id
        LEFT JOIN internal.support_users support_user
          ON support_user.id = outbox.client_user_id
        WHERE outbox.client_user_id IS NOT NULL
          AND (outbox.client_id IS DISTINCT FROM access.client_id
               OR support_user.id IS NULL
               OR support_user.client_id IS DISTINCT FROM outbox.client_id)
    ) THEN
        RAISE EXCEPTION 'Conversation outbox client participant tenant invariant failed';
    END IF;

    IF EXISTS (
        SELECT 1
        FROM digital.conversation_audit audit_row
        JOIN digital.conversation_access access
          ON access.conversation_id = audit_row.conversation_id
        LEFT JOIN internal.support_users support_user
          ON support_user.id = audit_row.client_user_id
        WHERE audit_row.client_user_id IS NOT NULL
          AND (audit_row.client_id IS DISTINCT FROM access.client_id
               OR support_user.id IS NULL
               OR support_user.client_id IS DISTINCT FROM audit_row.client_id)
    ) THEN
        RAISE EXCEPTION 'Conversation audit client actor tenant invariant failed';
    END IF;

    IF EXISTS (
        SELECT 1
        FROM digital.instructions message
        LEFT JOIN digital.instructions root ON root.id = message.instruction_id
        WHERE message.instruction_id IS NOT NULL
          AND (
              root.id IS NULL
              OR root.instruction_id IS DISTINCT FROM root.id
              OR message.client_id IS DISTINCT FROM root.client_id
              OR message.conversation_sequence IS NULL)
    ) THEN
        RAISE EXCEPTION 'Linked instruction root, tenant, or sequence invariant failed';
    END IF;

    IF EXISTS (
        SELECT instruction_id, conversation_sequence
        FROM digital.instructions
        WHERE instruction_id IS NOT NULL
        GROUP BY instruction_id, conversation_sequence
        HAVING count(*) > 1
    ) OR EXISTS (
        SELECT client_message_id
        FROM digital.instructions
        WHERE client_message_id IS NOT NULL
        GROUP BY client_message_id
        HAVING count(*) > 1
    ) THEN
        RAISE EXCEPTION 'Instruction sequence or idempotency uniqueness invariant failed';
    END IF;

    IF EXISTS (
        SELECT 1
        FROM digital.conversation_access access
        JOIN digital.instructions root ON root.id = access.conversation_id
        WHERE root.instruction_id IS DISTINCT FROM root.id
           OR root.client_id IS DISTINCT FROM access.client_id
           OR access.conversation_kind IS DISTINCT FROM CASE
                WHEN root.inst_type_id = 100 THEN 'Group'
                WHEN root.inst_type_id = 101 THEN 'Private'
                WHEN root.inst_type_id BETWEEN 110 AND 117
                     AND root.inst_category_id = 101 THEN 'Ticket'
                WHEN root.inst_type_id IN (121,122)
                     AND root.inst_category_id = 102 THEN 'Inquiry'
                ELSE NULL
              END
    ) THEN
        RAISE EXCEPTION 'Conversation access/root mapping invariant failed';
    END IF;

    IF EXISTS (
        SELECT 1
        FROM digital.instructions root
        LEFT JOIN digital.conversation_access access
          ON access.conversation_id = root.id
        LEFT JOIN digital.conversation_sequences allocator
          ON allocator.conversation_id = root.id
        WHERE root.instruction_id = root.id
          AND (
              root.inst_type_id IN (100,101)
              OR (root.inst_type_id BETWEEN 110 AND 117 AND root.inst_category_id = 101)
              OR (root.inst_type_id IN (121,122) AND root.inst_category_id = 102)
          )
          AND (access.conversation_id IS NULL OR allocator.conversation_id IS NULL)
    ) THEN
        RAISE EXCEPTION 'A canonical messaging/case root lacks access or allocator metadata';
    END IF;

    IF EXISTS (
        SELECT 1
        FROM digital.conversation_sequences allocator
        LEFT JOIN digital.instructions message
          ON message.instruction_id = allocator.conversation_id
        GROUP BY allocator.conversation_id, allocator.next_sequence
        HAVING allocator.next_sequence <= COALESCE(max(message.conversation_sequence), 0)
    ) THEN
        RAISE EXCEPTION 'A conversation allocator does not exceed committed history';
    END IF;

    IF EXISTS (
        SELECT client_id
        FROM digital.conversation_access
        WHERE conversation_kind = 'Group'
        GROUP BY client_id
        HAVING count(*) > 1
    ) OR EXISTS (
        SELECT client_id, client_user_id, admin_user_id
        FROM digital.conversation_access
        WHERE conversation_kind = 'Private' AND state = 'Active'
        GROUP BY client_id, client_user_id, admin_user_id
        HAVING count(*) > 1
    ) THEN
        RAISE EXCEPTION 'Group or active-private uniqueness invariant failed';
    END IF;

    IF EXISTS (
        SELECT 1
        FROM digital.attachments attachment
        JOIN digital.conversation_access access
          ON access.conversation_id = attachment.conversation_id
        LEFT JOIN digital.instructions message
          ON message.id = attachment.message_id
        LEFT JOIN internal.support_users uploader
          ON uploader.id = attachment.client_user_id
        WHERE access.client_id IS DISTINCT FROM attachment.client_id
           OR (attachment.client_user_id IS NOT NULL
               AND uploader.client_id IS DISTINCT FROM attachment.client_id)
           OR (attachment.message_id IS NOT NULL AND (
                  message.id IS NULL
                  OR message.instruction_id IS DISTINCT FROM attachment.conversation_id
                  OR message.client_id IS DISTINCT FROM attachment.client_id))
    ) THEN
        RAISE EXCEPTION 'Attachment conversation, uploader, or message scope invariant failed';
    END IF;

    IF EXISTS (
        SELECT 1
        FROM digital.attachment_audit audit_row
        JOIN digital.attachments attachment
          ON attachment.id = audit_row.attachment_id
        LEFT JOIN internal.support_users support_user
          ON support_user.id = audit_row.client_user_id
        WHERE audit_row.client_user_id IS NOT NULL
          AND (audit_row.client_id IS DISTINCT FROM attachment.client_id
               OR support_user.id IS NULL
               OR support_user.client_id IS DISTINCT FROM audit_row.client_id)
    ) THEN
        RAISE EXCEPTION 'Attachment audit client actor tenant invariant failed';
    END IF;

    IF EXISTS (
        SELECT 1
        FROM digital.attachments attachment
        WHERE attachment.state IN (
                'PendingUpload','Uploaded','StructuralValidation','StructurallyValidated',
                'Scanning','Promoting','Ready','DeletePending')
          AND attachment.reservation_bytes < GREATEST(
                attachment.declared_size, COALESCE(attachment.actual_size, 0))
    ) THEN
        RAISE EXCEPTION 'Active attachment reservation invariant failed';
    END IF;

    IF EXISTS (
        SELECT 1
        FROM digital.attachments attachment
        WHERE attachment.state IN ('StructurallyValidated','Promoting','Ready')
          AND (
              attachment.ready_key IS NULL
              OR attachment.source_etag IS NULL
              OR attachment.sha256 IS NULL
              OR attachment.actual_size IS NULL
              OR attachment.detected_media_type IS NULL)
    ) THEN
        RAISE EXCEPTION 'Structurally validated/promoting/ready attachment shape invariant failed';
    END IF;

    IF EXISTS (
        SELECT 1
        FROM digital.attachment_audit audit
        JOIN digital.attachments attachment ON attachment.id = audit.attachment_id
        WHERE audit.client_id IS DISTINCT FROM attachment.client_id
    ) THEN
        RAISE EXCEPTION 'Attachment audit tenant invariant failed';
    END IF;

    RAISE NOTICE
        'CBS Support messaging/attachment verification passed in database % for role shovan',
        current_database();
END
$data_invariants$;

COMMIT;
