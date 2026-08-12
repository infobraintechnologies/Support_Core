-- Manual deployment for shared test; target: test; application role: shovan.
-- Consolidates the listed Messaging V2/attachment migrations without the ledger.
-- Rerunnable only when the complete object set is compatible; partial deployments fail.
-- Preconditions: insert_user nullable; client_auth_user_id empty bigint; confirmed
-- integer internal.clients.id; blocking FKs identified; approved lock window; and
-- executor/application privileges for digital, admin, and internal are present.

BEGIN;

SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '15min';
SET LOCAL idle_in_transaction_session_timeout = '5min';

DO $database_guard$
BEGIN
    IF current_database() <> 'test' THEN
        RAISE EXCEPTION
            'Refusing CBS Support manual deployment: expected database test, connected to %',
            current_database();
    END IF;
END
$database_guard$;

CREATE TEMPORARY TABLE manual_deployment_state (
    deploy_required boolean NOT NULL
) ON COMMIT PRESERVE ROWS;

DO $preflight$
DECLARE
    owned_table_count integer;
    messaging_column_count integer;
    instructions_owner name;
    blocking_foreign_keys text;
BEGIN
    IF to_regnamespace('digital') IS NULL
       OR to_regnamespace('admin') IS NULL
       OR to_regnamespace('internal') IS NULL THEN
        RAISE EXCEPTION
            'Required schemas digital, admin, and internal must already exist';
    END IF;

    IF to_regclass('digital.instructions') IS NULL
       OR to_regclass('admin.users') IS NULL
       OR to_regclass('internal.support_users') IS NULL
       OR to_regclass('internal.clients') IS NULL THEN
        RAISE EXCEPTION
            'Required base tables digital.instructions, admin.users, internal.support_users, and internal.clients must already exist';
    END IF;

    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'shovan') THEN
        RAISE EXCEPTION 'Required application role shovan does not exist';
    END IF;

    SELECT count(*)
    INTO owned_table_count
    FROM unnest(ARRAY[
        'digital.conversation_access',
        'digital.conversation_sequences',
        'digital.conversation_read_cursors',
        'digital.conversation_outbox',
        'digital.conversation_audit',
        'digital.attachment_tenant_quotas',
        'digital.attachments',
        'digital.attachment_audit'
    ]) AS required(qualified_name)
    WHERE to_regclass(required.qualified_name) IS NOT NULL;

    SELECT count(*)
    INTO messaging_column_count
    FROM information_schema.columns
    WHERE table_schema = 'digital'
      AND table_name = 'instructions'
      AND column_name IN ('client_message_id', 'conversation_sequence');

    IF owned_table_count = 0 AND messaging_column_count = 0 THEN
        IF to_regprocedure('digital.maintain_attachment_quota_reservation()') IS NOT NULL
           OR to_regprocedure('digital.enforce_attachment_client_uploader_tenant()') IS NOT NULL THEN
            RAISE EXCEPTION
                'Partial deployment detected: attachment trigger functions exist while owned tables do not';
        END IF;

        INSERT INTO manual_deployment_state VALUES (true);
    ELSIF owned_table_count = 8 AND messaging_column_count = 2 THEN
        INSERT INTO manual_deployment_state VALUES (false);
    ELSE
        RAISE EXCEPTION
            'Partial deployment detected: found % of 8 owned tables and % of 2 instruction columns',
            owned_table_count,
            messaging_column_count;
    END IF;

    -- Fail before any durable change when the base types do not match the Dapper
    -- implementation and the referenced migration DDL.
    IF EXISTS (
        WITH expected(table_schema, table_name, column_name, formatted_type) AS (
            VALUES
                ('digital','instructions','id','bigint'),
                ('digital','instructions','instruction_id','bigint'),
                ('digital','instructions','client_id','bigint'),
                ('digital','instructions','insert_user','integer'),
                ('digital','instructions','inst_type_id','smallint'),
                ('digital','instructions','inst_category_id','smallint'),
                ('digital','instructions','instruction','text'),
                ('digital','instructions','datetime','timestamp with time zone'),
                ('digital','instructions','insert_date','timestamp with time zone'),
                ('admin','users','id','integer'),
                ('internal','clients','id','integer'),
                ('internal','support_users','id','integer'),
                ('internal','support_users','client_id','integer')
        )
        SELECT 1
        FROM expected
        LEFT JOIN pg_namespace n ON n.nspname = expected.table_schema
        LEFT JOIN pg_class c ON c.relnamespace = n.oid
                            AND c.relname = expected.table_name
                            AND c.relkind IN ('r','p')
        LEFT JOIN pg_attribute a ON a.attrelid = c.oid
                                AND a.attname = expected.column_name
                                AND a.attnum > 0
                                AND NOT a.attisdropped
        WHERE a.attname IS NULL
           OR format_type(a.atttypid, a.atttypmod) <> expected.formatted_type
    ) THEN
        RAISE EXCEPTION
            'A required base column is missing or has an incompatible data type';
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
            'Unexpected internal.support_users.user_id exists; the inspected test schema uses id as the canonical support-login identity';
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
        FROM pg_constraint key_constraint
        WHERE key_constraint.conrelid = 'internal.support_users'::regclass
          AND key_constraint.contype IN ('p','u')
          AND key_constraint.conkey = ARRAY[(
              SELECT attnum
              FROM pg_attribute
              WHERE attrelid = 'internal.support_users'::regclass
                AND attname = 'id'
                AND attnum > 0
                AND NOT attisdropped
          )]::smallint[]
    ) THEN
        RAISE EXCEPTION
            'internal.support_users.id must be a single-column primary or unique key';
    END IF;

    IF NOT EXISTS (
        SELECT 1
        FROM pg_attribute
        WHERE attrelid = 'internal.clients'::regclass
          AND attname = 'id'
          AND attnum > 0
          AND NOT attisdropped
          AND attnotnull
    ) OR NOT EXISTS (
        SELECT 1
        FROM pg_constraint key_constraint
        WHERE key_constraint.conrelid = 'internal.clients'::regclass
          AND key_constraint.contype IN ('p','u')
          AND key_constraint.conkey = ARRAY[(
              SELECT attnum
              FROM pg_attribute
              WHERE attrelid = 'internal.clients'::regclass
                AND attname = 'id'
                AND attnum > 0
                AND NOT attisdropped
          )]::smallint[]
    ) THEN
        RAISE EXCEPTION
            'internal.clients.id must be NOT NULL and a single-column primary or unique key';
    END IF;

    IF NOT EXISTS (
        SELECT 1
        FROM pg_attribute
        WHERE attrelid = 'digital.instructions'::regclass
          AND attname = 'client_auth_user_id'
          AND attnum > 0
          AND NOT attisdropped
    ) THEN
        RAISE EXCEPTION
            'Required digital.instructions.client_auth_user_id column is missing';
    END IF;

    IF format_type(
        (SELECT atttypid
         FROM pg_attribute
         WHERE attrelid = 'digital.instructions'::regclass
           AND attname = 'client_auth_user_id'
           AND attnum > 0
           AND NOT attisdropped),
        (SELECT atttypmod
         FROM pg_attribute
         WHERE attrelid = 'digital.instructions'::regclass
           AND attname = 'client_auth_user_id'
           AND attnum > 0
           AND NOT attisdropped)) NOT IN ('bigint','integer') THEN
        RAISE EXCEPTION
            'digital.instructions.client_auth_user_id must be the inspected bigint source type or corrected integer target type';
    END IF;

    IF EXISTS (
        SELECT 1
        FROM pg_attribute a
        WHERE a.attrelid = 'digital.instructions'::regclass
          AND a.attname = 'insert_user'
          AND a.attnum > 0
          AND NOT a.attisdropped
          AND a.attnotnull
    ) THEN
        RAISE EXCEPTION
            'digital.instructions.insert_user is expected to already be nullable; no nullability ALTER is generated';
    END IF;

    IF EXISTS (
        SELECT 1
        FROM digital.instructions
        WHERE client_auth_user_id IS NOT NULL
    ) AND format_type(
        (SELECT atttypid FROM pg_attribute
         WHERE attrelid = 'digital.instructions'::regclass
           AND attname = 'client_auth_user_id' AND attnum > 0 AND NOT attisdropped),
        (SELECT atttypmod FROM pg_attribute
         WHERE attrelid = 'digital.instructions'::regclass
           AND attname = 'client_auth_user_id' AND attnum > 0 AND NOT attisdropped)
    ) = 'bigint' THEN
        RAISE EXCEPTION
            'Refusing to narrow digital.instructions.client_auth_user_id from bigint while values exist';
    END IF;

    SELECT string_agg(c.conname, ', ' ORDER BY c.conname)
    INTO blocking_foreign_keys
    FROM pg_constraint c
    WHERE c.conrelid = 'digital.instructions'::regclass
      AND c.contype = 'f'
      AND c.conkey = ARRAY[(
          SELECT attnum
          FROM pg_attribute
          WHERE attrelid = 'digital.instructions'::regclass
            AND attname = 'client_auth_user_id'
            AND attnum > 0
            AND NOT attisdropped
      )]::smallint[];

    IF blocking_foreign_keys IS NOT NULL
       AND format_type(
           (SELECT atttypid FROM pg_attribute
            WHERE attrelid = 'digital.instructions'::regclass
              AND attname = 'client_auth_user_id' AND attnum > 0 AND NOT attisdropped),
           (SELECT atttypmod FROM pg_attribute
            WHERE attrelid = 'digital.instructions'::regclass
              AND attname = 'client_auth_user_id' AND attnum > 0 AND NOT attisdropped)
       ) = 'bigint' THEN
        RAISE EXCEPTION
            'The digital.instructions owner/DBA must drop client_auth_user_id foreign key(s) before the type correction: %',
            blocking_foreign_keys;
    END IF;

    IF EXISTS (
        SELECT 1
        FROM pg_constraint c
        WHERE c.conrelid = 'digital.instructions'::regclass
          AND c.contype = 'f'
          AND c.conkey = ARRAY[(
              SELECT attnum
              FROM pg_attribute
              WHERE attrelid = 'digital.instructions'::regclass
                AND attname = 'client_auth_user_id'
          )]::smallint[]
          AND c.confrelid <> 'internal.support_users'::regclass
    ) THEN
        RAISE EXCEPTION
            'An incompatible client_auth_user_id foreign key remains; the digital.instructions owner/DBA must remove the exact constraint before this script runs';
    END IF;

    IF (SELECT deploy_required FROM manual_deployment_state)
       OR format_type(
           (SELECT atttypid FROM pg_attribute
            WHERE attrelid = 'digital.instructions'::regclass
              AND attname = 'client_auth_user_id' AND attnum > 0 AND NOT attisdropped),
           (SELECT atttypmod FROM pg_attribute
            WHERE attrelid = 'digital.instructions'::regclass
              AND attname = 'client_auth_user_id' AND attnum > 0 AND NOT attisdropped)
       ) = 'bigint'
       OR NOT EXISTS (
           SELECT 1
           FROM pg_constraint constraint_row
           WHERE constraint_row.conrelid = 'digital.instructions'::regclass
             AND constraint_row.conname = 'fk_instructions_client_auth_support_user'
             AND constraint_row.contype = 'f'
             AND constraint_row.convalidated
       ) THEN
        SELECT owner_role.rolname
        INTO instructions_owner
        FROM pg_class base_table
        JOIN pg_roles owner_role ON owner_role.oid = base_table.relowner
        WHERE base_table.oid = 'digital.instructions'::regclass;

        IF instructions_owner <> current_user
           AND NOT pg_has_role(current_user, instructions_owner, 'USAGE')
           AND NOT (SELECT rolsuper FROM pg_roles WHERE rolname = current_user) THEN
            RAISE EXCEPTION
                'ALTER TABLE requires owner of digital.instructions (owner %, executor %); senior/DBA execution is required',
                instructions_owner,
                current_user;
        END IF;

        IF NOT has_schema_privilege(current_user, 'digital', 'USAGE')
           OR NOT has_schema_privilege(current_user, 'digital', 'CREATE')
           OR NOT has_schema_privilege(current_user, 'admin', 'USAGE')
           OR NOT has_schema_privilege(current_user, 'internal', 'USAGE') THEN
            RAISE EXCEPTION
                'Executor requires USAGE on digital/admin/internal and CREATE on digital';
        END IF;

        IF NOT has_table_privilege(current_user, 'admin.users', 'REFERENCES')
           OR NOT has_table_privilege(current_user, 'internal.support_users', 'REFERENCES') THEN
            RAISE EXCEPTION
                'Executor requires REFERENCES on admin.users and internal.support_users to create foreign keys';
        END IF;
    END IF;

    IF NOT has_table_privilege('shovan', 'admin.users', 'SELECT')
       OR NOT has_table_privilege('shovan', 'internal.support_users', 'SELECT')
       OR NOT has_table_privilege('shovan', 'internal.clients', 'SELECT') THEN
        RAISE EXCEPTION
            'The admin/internal table owners or DBA must grant SELECT on admin.users, internal.support_users, and internal.clients to role shovan';
    END IF;

    IF EXISTS (
        SELECT 1
        FROM digital.instructions instruction
        LEFT JOIN internal.support_users support_user
          ON support_user.id = instruction.client_auth_user_id
        WHERE instruction.client_auth_user_id IS NOT NULL
          AND support_user.id IS NULL
    ) THEN
        RAISE EXCEPTION
            'Existing client_auth_user_id values are missing from internal.support_users';
    END IF;

    IF EXISTS (
        SELECT 1
        FROM digital.instructions instruction
        JOIN internal.support_users support_user
          ON support_user.id = instruction.client_auth_user_id
        WHERE instruction.client_id IS DISTINCT FROM support_user.client_id
    ) THEN
        RAISE EXCEPTION
            'Existing client-authored instruction tenant differs from its support user tenant';
    END IF;

    IF EXISTS (
        SELECT 1
        FROM digital.instructions message
        LEFT JOIN digital.instructions root ON root.id = message.instruction_id
        WHERE message.instruction_id IS NOT NULL
          AND (root.id IS NULL OR root.instruction_id IS DISTINCT FROM root.id)
    ) THEN
        RAISE EXCEPTION
            'Every linked instruction must reference a canonical self-linked root';
    END IF;

    IF EXISTS (
        SELECT 1
        FROM digital.instructions message
        JOIN digital.instructions root ON root.id = message.instruction_id
        WHERE message.id <> root.id
          AND message.client_id IS DISTINCT FROM root.client_id
    ) THEN
        RAISE EXCEPTION 'A reply tenant differs from its root tenant';
    END IF;

    IF EXISTS (
        SELECT 1
        FROM digital.instructions
        WHERE instruction_id = id
          AND inst_type_id IN (100,101,110,111,112,113,114,115,116,117,121,122)
          AND client_id IS NULL
    ) THEN
        RAISE EXCEPTION 'A messaging/case root is missing client_id';
    END IF;

    IF EXISTS (
        SELECT 1
        FROM digital.instructions
        WHERE instruction_id = id
          AND inst_type_id = 100
        GROUP BY client_id
        HAVING count(*) > 1
    ) THEN
        RAISE EXCEPTION
            'More than one Group root exists for a tenant; explicit business remediation is required';
    END IF;

    IF EXISTS (
        SELECT 1
        FROM digital.instructions root
        WHERE root.instruction_id = root.id
          AND (
              (root.inst_type_id BETWEEN 110 AND 117
                 AND (root.inst_category_id IS DISTINCT FROM 101
                      OR NULLIF(btrim(root.instruction), '') IS NULL))
              OR
              (root.inst_type_id IN (121,122)
                 AND (root.inst_category_id IS DISTINCT FROM 102
                      OR NULLIF(btrim(root.instruction), '') IS NULL))
          )
    ) THEN
        RAISE EXCEPTION 'A canonical ticket/inquiry root has an invalid category or empty text';
    END IF;

    IF EXISTS (
        SELECT 1
        FROM digital.instructions message
        JOIN digital.instructions root ON root.id = message.instruction_id
        WHERE message.id <> root.id
          AND (
              (root.inst_type_id BETWEEN 110 AND 117 AND root.inst_category_id = 101)
              OR (root.inst_type_id IN (121,122) AND root.inst_category_id = 102)
          )
          AND (
              message.inst_type_id IS DISTINCT FROM root.inst_type_id
              OR message.inst_category_id IS DISTINCT FROM root.inst_category_id
          )
          AND NOT (
              message.inst_type_id IN (100, root.inst_type_id)
              AND message.inst_category_id IN (100, root.inst_category_id)
          )
    ) THEN
        RAISE EXCEPTION
            'A ticket/inquiry reply has an ambiguous type/category mismatch';
    END IF;
END
$preflight$;

DO $instruction_principal_type$
DECLARE
    current_type text;
    conflicting_foreign_keys text;
BEGIN
    SELECT format_type(column_row.atttypid, column_row.atttypmod)
    INTO current_type
    FROM pg_attribute column_row
    WHERE column_row.attrelid = 'digital.instructions'::regclass
      AND column_row.attname = 'client_auth_user_id'
      AND column_row.attnum > 0
      AND NOT column_row.attisdropped;

    IF current_type = 'bigint' THEN
        IF EXISTS (
            SELECT 1
            FROM digital.instructions
            WHERE client_auth_user_id IS NOT NULL
        ) THEN
            RAISE EXCEPTION
                'Refusing to narrow digital.instructions.client_auth_user_id from bigint while values exist';
        END IF;

        SELECT string_agg(
                   format('%I: %s', constraint_row.conname,
                       pg_get_constraintdef(constraint_row.oid)),
                   '; ' ORDER BY constraint_row.conname)
        INTO conflicting_foreign_keys
        FROM pg_constraint constraint_row
        WHERE constraint_row.conrelid = 'digital.instructions'::regclass
          AND constraint_row.contype = 'f'
          AND constraint_row.conkey = ARRAY[(
              SELECT column_row.attnum
              FROM pg_attribute column_row
              WHERE column_row.attrelid = 'digital.instructions'::regclass
                AND column_row.attname = 'client_auth_user_id'
                AND column_row.attnum > 0
                AND NOT column_row.attisdropped
          )]::smallint[];

        IF conflicting_foreign_keys IS NOT NULL THEN
            RAISE EXCEPTION
                'Cannot convert bigint client_auth_user_id while a blocking foreign key remains: %',
                conflicting_foreign_keys;
        END IF;

        ALTER TABLE digital.instructions
            ALTER COLUMN client_auth_user_id TYPE integer
            USING (
                CASE
                    WHEN client_auth_user_id IS NULL THEN NULL::integer
                    ELSE client_auth_user_id::integer
                END);
    ELSIF current_type IS DISTINCT FROM 'integer' THEN
        RAISE EXCEPTION
            'digital.instructions.client_auth_user_id must be bigint or integer, found %',
            COALESCE(current_type, '<missing>');
    END IF;
END
$instruction_principal_type$;

-- Reconcile the canonical Client identity FK independently of deploy_required.
-- This covers a rerun where Messaging V2 tables already exist but the prerequisite
-- FK was removed before converting client_auth_user_id.
DO $instruction_client_identity_fk$
DECLARE
    incompatible_foreign_keys text;
BEGIN
    SELECT string_agg(
               format('%I: %s', constraint_row.conname,
                   pg_get_constraintdef(constraint_row.oid)),
               '; ' ORDER BY constraint_row.conname)
    INTO incompatible_foreign_keys
    FROM pg_constraint constraint_row
    WHERE constraint_row.conrelid = 'digital.instructions'::regclass
      AND (
          constraint_row.conname = 'fk_instructions_client_auth_support_user'
          OR (
              constraint_row.contype = 'f'
              AND constraint_row.conkey = ARRAY[(
              SELECT column_row.attnum
              FROM pg_attribute column_row
              WHERE column_row.attrelid = 'digital.instructions'::regclass
                AND column_row.attname = 'client_auth_user_id'
                AND column_row.attnum > 0
                AND NOT column_row.attisdropped
              )]::smallint[]
          )
      )
      AND NOT (
          constraint_row.conname = 'fk_instructions_client_auth_support_user'
          AND constraint_row.contype = 'f'
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
          AND NOT constraint_row.condeferrable
          AND constraint_row.confupdtype = 'a'
          AND constraint_row.confdeltype = 'a'
          AND constraint_row.confmatchtype = 's'
      );

    IF incompatible_foreign_keys IS NOT NULL THEN
        RAISE EXCEPTION
            'Incompatible client_auth_user_id foreign key exists: %',
            incompatible_foreign_keys;
    END IF;

    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint constraint_row
        WHERE constraint_row.conrelid = 'digital.instructions'::regclass
          AND constraint_row.conname = 'fk_instructions_client_auth_support_user'
          AND constraint_row.contype = 'f'
    ) THEN
        ALTER TABLE digital.instructions
            ADD CONSTRAINT fk_instructions_client_auth_support_user
            FOREIGN KEY (client_auth_user_id)
            REFERENCES internal.support_users(id);
    ELSIF EXISTS (
        SELECT 1
        FROM pg_constraint constraint_row
        WHERE constraint_row.conrelid = 'digital.instructions'::regclass
          AND constraint_row.conname = 'fk_instructions_client_auth_support_user'
          AND constraint_row.contype = 'f'
          AND NOT constraint_row.convalidated
    ) THEN
        ALTER TABLE digital.instructions
            VALIDATE CONSTRAINT fk_instructions_client_auth_support_user;
    END IF;

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
            'Canonical client identity foreign key was not created and validated';
    END IF;
END
$instruction_client_identity_fk$;

DO $instruction_columns$
BEGIN
    IF (SELECT deploy_required FROM manual_deployment_state) THEN
        ALTER TABLE digital.instructions
            ADD COLUMN client_message_id uuid,
            ADD COLUMN conversation_sequence bigint,
            ADD CONSTRAINT uq_instructions_id_conversation_client
                UNIQUE (id, instruction_id, client_id);

        COMMENT ON COLUMN digital.instructions.client_message_id IS
            'Globally unique caller-generated idempotency key when present; NULL for legacy rows and conversation roots.';
        COMMENT ON COLUMN digital.instructions.conversation_sequence IS
            'Monotonic sequence within instruction_id. Empty roots are sequence-0 sentinels; visible messages use positive values.';
    END IF;
END
$instruction_columns$;

DO $create_tables$
BEGIN
    IF NOT (SELECT deploy_required FROM manual_deployment_state) THEN
        RETURN;
    END IF;

    CREATE TABLE digital.conversation_access (
        conversation_id bigint NOT NULL,
        client_id bigint NOT NULL,
        conversation_kind varchar(16) NOT NULL,
        state varchar(16) NOT NULL,
        client_user_id integer,
        admin_user_id integer,
        version bigint NOT NULL DEFAULT 1,
        created_at timestamptz NOT NULL DEFAULT now(),
        archived_at timestamptz,
        CONSTRAINT pk_conversation_access PRIMARY KEY (conversation_id),
        CONSTRAINT fk_conversation_access_conversation
            FOREIGN KEY (conversation_id) REFERENCES digital.instructions(id),
        CONSTRAINT fk_conversation_access_client_user
            FOREIGN KEY (client_user_id) REFERENCES internal.support_users(id),
        CONSTRAINT fk_conversation_access_admin_user
            FOREIGN KEY (admin_user_id) REFERENCES admin.users(id),
        CONSTRAINT uq_conversation_access_id_client UNIQUE (conversation_id, client_id),
        CONSTRAINT ck_conversation_access_kind
            CHECK (conversation_kind IN ('Group','Private','Ticket','Inquiry')),
        CONSTRAINT ck_conversation_access_state
            CHECK (state IN ('Active','Archived','NeedsReview')),
        CONSTRAINT ck_conversation_access_version_positive CHECK (version > 0),
        CONSTRAINT ck_conversation_access_archived_timestamp
            CHECK ((state = 'Archived') = (archived_at IS NOT NULL)),
        CONSTRAINT ck_conversation_access_participants CHECK (
            (conversation_kind IN ('Group','Ticket','Inquiry')
                AND state = 'Active'
                AND client_user_id IS NULL
                AND admin_user_id IS NULL)
            OR
            (conversation_kind = 'Private'
                AND (
                    state = 'NeedsReview'
                    OR (state IN ('Active','Archived')
                        AND client_user_id IS NOT NULL
                        AND admin_user_id IS NOT NULL)
                ))
        )
    );

    CREATE TABLE digital.conversation_sequences (
        conversation_id bigint NOT NULL,
        next_sequence bigint NOT NULL,
        CONSTRAINT pk_conversation_sequences PRIMARY KEY (conversation_id),
        CONSTRAINT fk_conversation_sequences_conversation
            FOREIGN KEY (conversation_id) REFERENCES digital.instructions(id),
        CONSTRAINT ck_conversation_sequences_next_positive CHECK (next_sequence >= 1)
    );

    CREATE TABLE digital.conversation_read_cursors (
        read_cursor_id bigint GENERATED BY DEFAULT AS IDENTITY,
        conversation_id bigint NOT NULL,
        principal_kind varchar(16) NOT NULL,
        admin_user_id integer,
        client_user_id integer,
        last_read_sequence bigint NOT NULL DEFAULT 0,
        updated_at timestamptz NOT NULL DEFAULT now(),
        CONSTRAINT pk_conversation_read_cursors PRIMARY KEY (read_cursor_id),
        CONSTRAINT fk_conversation_read_cursors_access
            FOREIGN KEY (conversation_id) REFERENCES digital.conversation_access(conversation_id),
        CONSTRAINT fk_conversation_read_cursors_admin_user
            FOREIGN KEY (admin_user_id) REFERENCES admin.users(id),
        CONSTRAINT fk_conversation_read_cursors_client_user
            FOREIGN KEY (client_user_id) REFERENCES internal.support_users(id),
        CONSTRAINT ck_conversation_read_cursors_principal_kind
            CHECK (principal_kind IN ('Admin','Client')),
        CONSTRAINT ck_conversation_read_cursors_principal CHECK (
            (principal_kind = 'Admin' AND admin_user_id IS NOT NULL AND client_user_id IS NULL)
            OR
            (principal_kind = 'Client' AND admin_user_id IS NULL AND client_user_id IS NOT NULL)
        ),
        CONSTRAINT ck_conversation_read_cursors_sequence_nonnegative
            CHECK (last_read_sequence >= 0)
    );

    CREATE TABLE digital.conversation_outbox (
        event_id uuid NOT NULL,
        conversation_id bigint NOT NULL,
        client_id bigint NOT NULL,
        conversation_kind varchar(16) NOT NULL,
        conversation_state varchar(16) NOT NULL,
        client_user_id integer,
        admin_user_id integer,
        access_version bigint NOT NULL,
        message_id bigint,
        event_type varchar(64) NOT NULL,
        schema_version smallint NOT NULL DEFAULT 1,
        payload jsonb NOT NULL,
        occurred_at timestamptz NOT NULL DEFAULT now(),
        available_at timestamptz NOT NULL DEFAULT now(),
        attempt_count integer NOT NULL DEFAULT 0,
        lease_owner varchar(128),
        lease_until timestamptz,
        processed_at timestamptz,
        dead_lettered_at timestamptz,
        last_error_code varchar(64),
        CONSTRAINT pk_conversation_outbox PRIMARY KEY (event_id),
        CONSTRAINT fk_conversation_outbox_access
            FOREIGN KEY (conversation_id) REFERENCES digital.conversation_access(conversation_id),
        CONSTRAINT fk_conversation_outbox_client_user
            FOREIGN KEY (client_user_id) REFERENCES internal.support_users(id),
        CONSTRAINT fk_conversation_outbox_admin_user
            FOREIGN KEY (admin_user_id) REFERENCES admin.users(id),
        CONSTRAINT fk_conversation_outbox_message
            FOREIGN KEY (message_id) REFERENCES digital.instructions(id),
        CONSTRAINT ck_conversation_outbox_event_type_nonempty CHECK (btrim(event_type) <> ''),
        CONSTRAINT ck_conversation_outbox_kind
            CHECK (conversation_kind IN ('Group','Private','Ticket','Inquiry')),
        CONSTRAINT ck_conversation_outbox_state
            CHECK (conversation_state IN ('Active','Archived','NeedsReview')),
        CONSTRAINT ck_conversation_outbox_participants CHECK (
            (conversation_kind IN ('Group','Ticket','Inquiry')
                AND client_user_id IS NULL AND admin_user_id IS NULL)
            OR
            (conversation_kind = 'Private'
                AND client_user_id IS NOT NULL AND admin_user_id IS NOT NULL)
        ),
        CONSTRAINT ck_conversation_outbox_access_version_positive CHECK (access_version > 0),
        CONSTRAINT ck_conversation_outbox_schema_version_positive CHECK (schema_version > 0),
        CONSTRAINT ck_conversation_outbox_payload_object CHECK (jsonb_typeof(payload) = 'object'),
        CONSTRAINT ck_conversation_outbox_availability_order CHECK (available_at >= occurred_at),
        CONSTRAINT ck_conversation_outbox_attempt_nonnegative CHECK (attempt_count >= 0),
        CONSTRAINT ck_conversation_outbox_lease_pair CHECK ((lease_owner IS NULL) = (lease_until IS NULL)),
        CONSTRAINT ck_conversation_outbox_lease_owner_nonempty
            CHECK (lease_owner IS NULL OR btrim(lease_owner) <> ''),
        CONSTRAINT ck_conversation_outbox_terminal_exclusive
            CHECK (processed_at IS NULL OR dead_lettered_at IS NULL),
        CONSTRAINT ck_conversation_outbox_terminal_not_leased CHECK (
            (processed_at IS NULL AND dead_lettered_at IS NULL)
            OR (lease_owner IS NULL AND lease_until IS NULL)
        ),
        CONSTRAINT ck_conversation_outbox_error_code_nonempty
            CHECK (last_error_code IS NULL OR btrim(last_error_code) <> '')
    );

    CREATE TABLE digital.conversation_audit (
        audit_id bigint GENERATED BY DEFAULT AS IDENTITY,
        conversation_id bigint NOT NULL,
        client_id bigint NOT NULL,
        action varchar(64) NOT NULL,
        actor_kind varchar(16) NOT NULL,
        admin_user_id integer,
        client_user_id integer,
        occurred_at timestamptz NOT NULL DEFAULT now(),
        details jsonb NOT NULL DEFAULT '{}'::jsonb,
        CONSTRAINT pk_conversation_audit PRIMARY KEY (audit_id),
        CONSTRAINT fk_conversation_audit_access
            FOREIGN KEY (conversation_id) REFERENCES digital.conversation_access(conversation_id),
        CONSTRAINT fk_conversation_audit_admin_user
            FOREIGN KEY (admin_user_id) REFERENCES admin.users(id),
        CONSTRAINT fk_conversation_audit_client_user
            FOREIGN KEY (client_user_id) REFERENCES internal.support_users(id),
        CONSTRAINT ck_conversation_audit_action_nonempty CHECK (btrim(action) <> ''),
        CONSTRAINT ck_conversation_audit_actor_kind
            CHECK (actor_kind IN ('Admin','Client','System')),
        CONSTRAINT ck_conversation_audit_actor CHECK (
            (actor_kind = 'Admin' AND admin_user_id IS NOT NULL AND client_user_id IS NULL)
            OR
            (actor_kind = 'Client' AND admin_user_id IS NULL AND client_user_id IS NOT NULL)
            OR
            (actor_kind = 'System' AND admin_user_id IS NULL AND client_user_id IS NULL)
        ),
        CONSTRAINT ck_conversation_audit_details_object CHECK (jsonb_typeof(details) = 'object')
    );

    CREATE TABLE digital.attachment_tenant_quotas (
        client_id bigint NOT NULL,
        active_storage_limit_bytes bigint NOT NULL,
        updated_at timestamptz NOT NULL DEFAULT now(),
        CONSTRAINT pk_attachment_tenant_quotas PRIMARY KEY (client_id),
        CONSTRAINT ck_attachment_tenant_quota_minimum
            CHECK (active_storage_limit_bytes >= 1073741824)
    );

    CREATE TABLE digital.attachments (
        id uuid NOT NULL,
        client_id bigint NOT NULL,
        conversation_id bigint NOT NULL,
        message_id bigint,
        position smallint,
        admin_user_id integer,
        client_user_id integer,
        state varchar(32) NOT NULL,
        quarantine_key varchar(512),
        ready_key varchar(512),
        display_name varchar(255) NOT NULL,
        declared_media_type varchar(128) NOT NULL,
        detected_media_type varchar(128),
        declared_size bigint NOT NULL,
        actual_size bigint,
        reservation_bytes bigint NOT NULL,
        source_etag varchar(256),
        expected_ready_etag varchar(256),
        sha256 bytea,
        created_at timestamptz NOT NULL DEFAULT now(),
        updated_at timestamptz NOT NULL DEFAULT now(),
        upload_completed_at timestamptz,
        ready_at timestamptz,
        bound_at timestamptz,
        expires_at timestamptz,
        deleted_at timestamptz,
        lease_owner varchar(128),
        lease_until timestamptz,
        attempt_count integer NOT NULL DEFAULT 0,
        next_attempt_at timestamptz NOT NULL DEFAULT now(),
        rejection_code varchar(64),
        delete_target_state varchar(32),
        last_error_code varchar(64),
        deletion_attempt_count integer NOT NULL DEFAULT 0,
        CONSTRAINT pk_attachments PRIMARY KEY (id),
        CONSTRAINT fk_attachments_conversation
            FOREIGN KEY (conversation_id) REFERENCES digital.conversation_access(conversation_id),
        CONSTRAINT fk_attachments_message
            FOREIGN KEY (message_id) REFERENCES digital.instructions(id),
        CONSTRAINT fk_attachments_admin_user
            FOREIGN KEY (admin_user_id) REFERENCES admin.users(id),
        CONSTRAINT fk_attachments_client_user
            FOREIGN KEY (client_user_id) REFERENCES internal.support_users(id),
        CONSTRAINT uq_attachments_id_client UNIQUE (id, client_id),
        CONSTRAINT fk_attachments_conversation_client
            FOREIGN KEY (conversation_id, client_id)
            REFERENCES digital.conversation_access(conversation_id, client_id),
        CONSTRAINT fk_attachments_message_conversation_client
            FOREIGN KEY (message_id, conversation_id, client_id)
            REFERENCES digital.instructions(id, instruction_id, client_id),
        CONSTRAINT ck_attachments_state CHECK (state IN (
            'PendingUpload','Uploaded','StructuralValidation','StructurallyValidated',
            'Scanning','Promoting','Ready','Rejected','ScanFailed',
            'DeletePending','Deleted','Expired')),
        CONSTRAINT ck_attachments_uploader CHECK (
            (admin_user_id IS NOT NULL AND client_user_id IS NULL)
            OR (admin_user_id IS NULL AND client_user_id IS NOT NULL)),
        CONSTRAINT ck_attachments_sizes CHECK (
            declared_size BETWEEN 1 AND 10485760
            AND (actual_size IS NULL OR actual_size >= 0)
            AND reservation_bytes >= 0
            AND (
                state NOT IN (
                    'PendingUpload','Uploaded','StructuralValidation','StructurallyValidated',
                    'Scanning','Promoting','Ready','DeletePending')
                OR reservation_bytes >= GREATEST(declared_size, COALESCE(actual_size, 0))
            )),
        CONSTRAINT ck_attachments_position CHECK (
            (message_id IS NULL AND position IS NULL AND bound_at IS NULL)
            OR (message_id IS NOT NULL AND position BETWEEN 1 AND 5 AND bound_at IS NOT NULL)),
        CONSTRAINT ck_attachments_lease_pair CHECK ((lease_owner IS NULL) = (lease_until IS NULL)),
        CONSTRAINT ck_attachments_attempt_nonnegative CHECK (attempt_count >= 0),
        CONSTRAINT ck_attachments_rejection_code
            CHECK (rejection_code IS NULL OR btrim(rejection_code) <> ''),
        CONSTRAINT ck_attachments_delete_target CHECK (
            (state = 'DeletePending'
                AND delete_target_state IN ('Deleted','Expired','Rejected','ScanFailed'))
            OR (state <> 'DeletePending' AND delete_target_state IS NULL)),
        CONSTRAINT ck_attachments_ready_shape CHECK (
            state NOT IN ('StructurallyValidated','Ready','Promoting')
            OR (ready_key IS NOT NULL
                AND source_etag IS NOT NULL
                AND sha256 IS NOT NULL
                AND actual_size IS NOT NULL
                AND detected_media_type IS NOT NULL)),
        CONSTRAINT ck_attachments_terminal_reservation CHECK (
            state NOT IN ('Rejected','ScanFailed','Deleted','Expired')
            OR reservation_bytes = 0),
        CONSTRAINT ck_attachments_last_error_code
            CHECK (last_error_code IS NULL OR btrim(last_error_code) <> ''),
        CONSTRAINT ck_attachments_deletion_attempt_nonnegative
            CHECK (deletion_attempt_count >= 0),
        CONSTRAINT ck_attachments_bound_retention CHECK (
            message_id IS NULL
            OR (expires_at IS NOT NULL AND expires_at >= bound_at + INTERVAL '365 days')),
        CONSTRAINT ck_attachments_ready_unbound_retention CHECK (
            state <> 'Ready'
            OR message_id IS NOT NULL
            OR (ready_at IS NOT NULL
                AND expires_at IS NOT NULL
                AND expires_at >= ready_at + INTERVAL '24 hours'))
    );

    CREATE TABLE digital.attachment_audit (
        audit_id bigint GENERATED BY DEFAULT AS IDENTITY,
        attachment_id uuid NOT NULL,
        client_id bigint NOT NULL,
        action varchar(64) NOT NULL,
        actor_kind varchar(16) NOT NULL,
        admin_user_id integer,
        client_user_id integer,
        occurred_at timestamptz NOT NULL DEFAULT now(),
        details jsonb NOT NULL DEFAULT '{}'::jsonb,
        CONSTRAINT pk_attachment_audit PRIMARY KEY (audit_id),
        CONSTRAINT fk_attachment_audit_attachment
            FOREIGN KEY (attachment_id) REFERENCES digital.attachments(id),
        CONSTRAINT fk_attachment_audit_admin_user
            FOREIGN KEY (admin_user_id) REFERENCES admin.users(id),
        CONSTRAINT fk_attachment_audit_client_user
            FOREIGN KEY (client_user_id) REFERENCES internal.support_users(id),
        CONSTRAINT fk_attachment_audit_attachment_client
            FOREIGN KEY (attachment_id, client_id) REFERENCES digital.attachments(id, client_id),
        CONSTRAINT ck_attachment_audit_action CHECK (btrim(action) <> ''),
        CONSTRAINT ck_attachment_audit_actor_kind
            CHECK (actor_kind IN ('Admin','Client','System')),
        CONSTRAINT ck_attachment_audit_actor CHECK (
            (actor_kind = 'Admin' AND admin_user_id IS NOT NULL AND client_user_id IS NULL)
            OR
            (actor_kind = 'Client' AND admin_user_id IS NULL AND client_user_id IS NOT NULL)
            OR
            (actor_kind = 'System' AND admin_user_id IS NULL AND client_user_id IS NULL)),
        CONSTRAINT ck_attachment_audit_details CHECK (jsonb_typeof(details) = 'object')
    );
END
$create_tables$;

-- Canonical Client-user identities are int4 because they reference
-- internal.support_users(id). Fresh tables above already use integer. This block
-- repairs the previously committed manual-deployment shape where these six columns
-- were bigint, and it intentionally runs independently of deploy_required.
DO $client_user_identity_columns$
DECLARE
    validation_failure text;
    incompatible_foreign_keys text;
    current_type text;
    table_owner name;
    client_user_attnum smallint;
    support_user_id_attnum smallint;
    attachment_client_user_attnum smallint;
    attachment_client_id_attnum smallint;
    attachment_table_owner name;
    attachment_trigger_function_oid oid;
    attachment_trigger_was_dropped boolean := false;
    deployment_creates_schema boolean;
    unexpected_attachment_triggers text;
    needs_repair boolean;
    correction record;
BEGIN
    SELECT deploy_required
    INTO deployment_creates_schema
    FROM manual_deployment_state;

    IF EXISTS (
        WITH expected(table_oid, table_name) AS (
            VALUES
                ('digital.conversation_access'::regclass, 'conversation_access'),
                ('digital.conversation_read_cursors'::regclass, 'conversation_read_cursors'),
                ('digital.conversation_outbox'::regclass, 'conversation_outbox'),
                ('digital.conversation_audit'::regclass, 'conversation_audit'),
                ('digital.attachments'::regclass, 'attachments'),
                ('digital.attachment_audit'::regclass, 'attachment_audit')
        )
        SELECT 1
        FROM expected
        LEFT JOIN pg_attribute column_row
          ON column_row.attrelid = expected.table_oid
         AND column_row.attname = 'client_user_id'
         AND column_row.attnum > 0
         AND NOT column_row.attisdropped
        WHERE column_row.attname IS NULL
           OR format_type(column_row.atttypid, column_row.atttypmod)
              NOT IN ('bigint', 'integer')
    ) THEN
        RAISE EXCEPTION
            'Every owned client_user_id column must be bigint legacy state or integer canonical state';
    END IF;

    -- Validate every populated value before any FK is dropped or any column is
    -- narrowed. int4 comparison is performed by widening support_users.id/client_id
    -- to bigint, so an out-of-range legacy value is never cast down during checks.
    WITH candidates(table_name, record_key, client_user_id, client_id) AS (
        SELECT 'conversation_access', access.conversation_id::text,
               access.client_user_id::bigint, access.client_id
        FROM digital.conversation_access access
        WHERE access.client_user_id IS NOT NULL
        UNION ALL
        SELECT 'conversation_read_cursors', cursor_row.read_cursor_id::text,
               cursor_row.client_user_id::bigint, access.client_id
        FROM digital.conversation_read_cursors cursor_row
        LEFT JOIN digital.conversation_access access
          ON access.conversation_id = cursor_row.conversation_id
        WHERE cursor_row.client_user_id IS NOT NULL
        UNION ALL
        SELECT 'conversation_outbox', outbox.event_id::text,
               outbox.client_user_id::bigint, outbox.client_id
        FROM digital.conversation_outbox outbox
        WHERE outbox.client_user_id IS NOT NULL
        UNION ALL
        SELECT 'conversation_audit', audit_row.audit_id::text,
               audit_row.client_user_id::bigint, audit_row.client_id
        FROM digital.conversation_audit audit_row
        WHERE audit_row.client_user_id IS NOT NULL
        UNION ALL
        SELECT 'attachments', attachment.id::text,
               attachment.client_user_id::bigint, attachment.client_id
        FROM digital.attachments attachment
        WHERE attachment.client_user_id IS NOT NULL
        UNION ALL
        SELECT 'attachment_audit', audit_row.audit_id::text,
               audit_row.client_user_id::bigint, audit_row.client_id
        FROM digital.attachment_audit audit_row
        WHERE audit_row.client_user_id IS NOT NULL
    )
    SELECT CASE
               WHEN candidate.client_user_id NOT BETWEEN -2147483648 AND 2147483647
                   THEN format(
                       'digital.%s row %s client_user_id %s is outside PostgreSQL integer range',
                       candidate.table_name, candidate.record_key, candidate.client_user_id)
               WHEN support_user.id IS NULL
                   THEN format(
                       'digital.%s row %s client_user_id %s does not reference internal.support_users(id)',
                       candidate.table_name, candidate.record_key, candidate.client_user_id)
               WHEN support_user.client_id::bigint IS DISTINCT FROM candidate.client_id
                   THEN format(
                       'digital.%s row %s client_user_id %s belongs to client %s, expected client %s',
                       candidate.table_name, candidate.record_key, candidate.client_user_id,
                       support_user.client_id, candidate.client_id)
           END
    INTO validation_failure
    FROM candidates candidate
    LEFT JOIN internal.support_users support_user
      ON support_user.id::bigint = candidate.client_user_id
    WHERE candidate.client_user_id NOT BETWEEN -2147483648 AND 2147483647
       OR support_user.id IS NULL
       OR support_user.client_id::bigint IS DISTINCT FROM candidate.client_id
    ORDER BY CASE
                 WHEN candidate.client_user_id NOT BETWEEN -2147483648 AND 2147483647 THEN 1
                 WHEN support_user.id IS NULL THEN 2
                 ELSE 3
             END,
             candidate.table_name,
             candidate.record_key
    LIMIT 1;

    IF validation_failure IS NOT NULL THEN
        RAISE EXCEPTION 'Client-user identity narrowing preflight failed: %', validation_failure;
    END IF;

    SELECT column_row.attnum
    INTO support_user_id_attnum
    FROM pg_attribute column_row
    WHERE column_row.attrelid = 'internal.support_users'::regclass
      AND column_row.attname = 'id'
      AND column_row.attnum > 0
      AND NOT column_row.attisdropped;

    -- ALTER COLUMN TYPE cannot run while an UPDATE OF client_user_id trigger
    -- depends on the legacy bigint column. Inspect that dependency before any DDL:
    -- only the reviewed tenant trigger may be removed, and only during the
    -- attachment-column repair below. The quota trigger deliberately is not
    -- considered a client_user_id dependency and is never dropped here.
    SELECT client_user_column.attnum,
           client_id_column.attnum,
           owner_role.rolname
    INTO attachment_client_user_attnum,
         attachment_client_id_attnum,
         attachment_table_owner
    FROM pg_attribute client_user_column
    JOIN pg_attribute client_id_column
      ON client_id_column.attrelid = client_user_column.attrelid
     AND client_id_column.attname = 'client_id'
     AND client_id_column.attnum > 0
     AND NOT client_id_column.attisdropped
    JOIN pg_class attachment_table
      ON attachment_table.oid = client_user_column.attrelid
    JOIN pg_roles owner_role
      ON owner_role.oid = attachment_table.relowner
    WHERE client_user_column.attrelid = 'digital.attachments'::regclass
      AND client_user_column.attname = 'client_user_id'
      AND client_user_column.attnum > 0
      AND NOT client_user_column.attisdropped;

    attachment_trigger_function_oid :=
        to_regprocedure('digital.enforce_attachment_client_uploader_tenant()');

    SELECT string_agg(
               format('%I: %s', trigger_row.tgname,
                      pg_get_triggerdef(trigger_row.oid)),
               '; ' ORDER BY trigger_row.tgname)
    INTO unexpected_attachment_triggers
    FROM pg_trigger trigger_row
    WHERE trigger_row.tgrelid = 'digital.attachments'::regclass
      AND NOT trigger_row.tgisinternal
      AND position(
          format(' %s ', attachment_client_user_attnum)
          IN format(' %s ', trigger_row.tgattr::text)) > 0
      AND trigger_row.tgname <> 'trg_attachments_client_uploader_tenant';

    IF unexpected_attachment_triggers IS NOT NULL THEN
        RAISE EXCEPTION
            'Unexpected dependent trigger on digital.attachments.client_user_id: %',
            unexpected_attachment_triggers;
    END IF;

    IF EXISTS (
        SELECT 1
        FROM pg_trigger trigger_row
        WHERE trigger_row.tgrelid = 'digital.attachments'::regclass
          AND trigger_row.tgname = 'trg_attachments_client_uploader_tenant'
          AND NOT (
              NOT trigger_row.tgisinternal
              AND attachment_trigger_function_oid IS NOT NULL
              AND trigger_row.tgfoid = attachment_trigger_function_oid
              AND trigger_row.tgtype = 23
              AND trigger_row.tgattr::text = format(
                  '%s %s', attachment_client_user_attnum, attachment_client_id_attnum)
              AND trigger_row.tgenabled = 'O'
              AND trigger_row.tgnargs = 0
          )
    ) THEN
        RAISE EXCEPTION
            'Incompatible trg_attachments_client_uploader_tenant definition on digital.attachments';
    END IF;

    IF attachment_trigger_function_oid IS NULL
       AND NOT deployment_creates_schema THEN
        RAISE EXCEPTION
            'Missing digital.enforce_attachment_client_uploader_tenant(); cannot reconcile attachment tenant trigger';
    END IF;

    -- Validate every constraint shape and every required ownership privilege before
    -- issuing the first ALTER TABLE. Unexpected FKs are never dropped automatically.
    FOR correction IN
        SELECT *
        FROM (VALUES
            ('digital.conversation_access'::regclass, 'fk_conversation_access_client_user'),
            ('digital.conversation_read_cursors'::regclass, 'fk_conversation_read_cursors_client_user'),
            ('digital.conversation_outbox'::regclass, 'fk_conversation_outbox_client_user'),
            ('digital.conversation_audit'::regclass, 'fk_conversation_audit_client_user'),
            ('digital.attachments'::regclass, 'fk_attachments_client_user'),
            ('digital.attachment_audit'::regclass, 'fk_attachment_audit_client_user')
        ) AS expected(table_oid, constraint_name)
    LOOP
        SELECT format_type(column_row.atttypid, column_row.atttypmod),
               column_row.attnum,
               owner_role.rolname
        INTO current_type, client_user_attnum, table_owner
        FROM pg_attribute column_row
        JOIN pg_class table_row ON table_row.oid = column_row.attrelid
        JOIN pg_roles owner_role ON owner_role.oid = table_row.relowner
        WHERE column_row.attrelid = correction.table_oid
          AND column_row.attname = 'client_user_id'
          AND column_row.attnum > 0
          AND NOT column_row.attisdropped;

        SELECT string_agg(
                   format('%I: %s', constraint_row.conname,
                       pg_get_constraintdef(constraint_row.oid)),
                   '; ' ORDER BY constraint_row.conname)
        INTO incompatible_foreign_keys
        FROM pg_constraint constraint_row
        WHERE constraint_row.conrelid = correction.table_oid
          AND (
              constraint_row.conname = correction.constraint_name
              OR (
                  constraint_row.contype = 'f'
                  AND constraint_row.conkey = ARRAY[client_user_attnum]::smallint[]
              )
          )
          AND NOT (
              constraint_row.conname = correction.constraint_name
              AND constraint_row.contype = 'f'
              AND constraint_row.conkey = ARRAY[client_user_attnum]::smallint[]
              AND constraint_row.confrelid = 'internal.support_users'::regclass
              AND constraint_row.confkey = ARRAY[support_user_id_attnum]::smallint[]
              AND NOT constraint_row.condeferrable
              AND constraint_row.confupdtype = 'a'
              AND constraint_row.confdeltype = 'a'
              AND constraint_row.confmatchtype = 's'
          );

        IF incompatible_foreign_keys IS NOT NULL THEN
            RAISE EXCEPTION
                'Incompatible client_user_id foreign key on %: %',
                correction.table_oid::regclass,
                incompatible_foreign_keys;
        END IF;

        SELECT current_type = 'bigint' OR NOT EXISTS (
                   SELECT 1
                   FROM pg_constraint constraint_row
                   WHERE constraint_row.conrelid = correction.table_oid
                     AND constraint_row.conname = correction.constraint_name
                     AND constraint_row.contype = 'f'
                     AND constraint_row.convalidated
               )
        INTO needs_repair;

        IF needs_repair
           AND table_owner <> current_user
           AND NOT pg_has_role(current_user, table_owner, 'USAGE')
           AND NOT (SELECT rolsuper FROM pg_roles WHERE rolname = current_user) THEN
            RAISE EXCEPTION
                'ALTER TABLE requires owner of % (owner %, executor %)',
                correction.table_oid::regclass,
                table_owner,
                current_user;
        END IF;

        IF needs_repair
           AND NOT has_table_privilege(
               current_user, 'internal.support_users', 'REFERENCES') THEN
            RAISE EXCEPTION
                'Executor requires REFERENCES on internal.support_users to repair %',
                correction.table_oid::regclass;
        END IF;
    END LOOP;

    -- All data, FK, and privilege checks above completed without DDL. Now narrow
    -- each legacy bigint column, recreate only its expected FK, and validate it.
    FOR correction IN
        SELECT *
        FROM (VALUES
            ('digital.conversation_access'::regclass, 'fk_conversation_access_client_user'),
            ('digital.conversation_read_cursors'::regclass, 'fk_conversation_read_cursors_client_user'),
            ('digital.conversation_outbox'::regclass, 'fk_conversation_outbox_client_user'),
            ('digital.conversation_audit'::regclass, 'fk_conversation_audit_client_user'),
            ('digital.attachments'::regclass, 'fk_attachments_client_user'),
            ('digital.attachment_audit'::regclass, 'fk_attachment_audit_client_user')
        ) AS expected(table_oid, constraint_name)
    LOOP
        SELECT format_type(column_row.atttypid, column_row.atttypmod),
               column_row.attnum
        INTO current_type, client_user_attnum
        FROM pg_attribute column_row
        WHERE column_row.attrelid = correction.table_oid
          AND column_row.attname = 'client_user_id'
          AND column_row.attnum > 0
          AND NOT column_row.attisdropped;

        IF current_type = 'bigint' THEN
            IF correction.table_oid = 'digital.attachments'::regclass
               AND EXISTS (
                   SELECT 1
                   FROM pg_trigger trigger_row
                   WHERE trigger_row.tgrelid = 'digital.attachments'::regclass
                     AND trigger_row.tgname = 'trg_attachments_client_uploader_tenant'
                     AND NOT trigger_row.tgisinternal
                     AND trigger_row.tgfoid = attachment_trigger_function_oid
                     AND trigger_row.tgtype = 23
                     AND trigger_row.tgattr::text = format(
                         '%s %s', attachment_client_user_attnum, attachment_client_id_attnum)
                     AND trigger_row.tgenabled = 'O'
                     AND trigger_row.tgnargs = 0
               ) THEN
                -- Keep the trigger function. The transaction recreates this exact
                -- trigger after the column has been narrowed and before final
                -- validation; any later failure rolls back both operations.
                DROP TRIGGER trg_attachments_client_uploader_tenant ON digital.attachments;
                attachment_trigger_was_dropped := true;
            END IF;

            IF EXISTS (
                SELECT 1
                FROM pg_constraint constraint_row
                WHERE constraint_row.conrelid = correction.table_oid
                  AND constraint_row.conname = correction.constraint_name
                  AND constraint_row.contype = 'f'
            ) THEN
                EXECUTE format(
                    'ALTER TABLE %s DROP CONSTRAINT %I',
                    correction.table_oid::regclass,
                    correction.constraint_name);
            END IF;

            EXECUTE format(
                'ALTER TABLE %s ALTER COLUMN client_user_id TYPE integer USING (CASE WHEN client_user_id IS NULL THEN NULL::integer ELSE client_user_id::integer END)',
                correction.table_oid::regclass);
        END IF;

        IF NOT EXISTS (
            SELECT 1
            FROM pg_constraint constraint_row
            WHERE constraint_row.conrelid = correction.table_oid
              AND constraint_row.conname = correction.constraint_name
              AND constraint_row.contype = 'f'
        ) THEN
            EXECUTE format(
                'ALTER TABLE %s ADD CONSTRAINT %I FOREIGN KEY (client_user_id) REFERENCES internal.support_users(id) NOT VALID',
                correction.table_oid::regclass,
                correction.constraint_name);
        END IF;

        IF EXISTS (
            SELECT 1
            FROM pg_constraint constraint_row
            WHERE constraint_row.conrelid = correction.table_oid
              AND constraint_row.conname = correction.constraint_name
              AND constraint_row.contype = 'f'
              AND NOT constraint_row.convalidated
        ) THEN
            EXECUTE format(
                'ALTER TABLE %s VALIDATE CONSTRAINT %I',
                correction.table_oid::regclass,
                correction.constraint_name);
        END IF;

        IF NOT EXISTS (
            SELECT 1
            FROM pg_constraint constraint_row
            JOIN pg_attribute source_column
              ON source_column.attrelid = constraint_row.conrelid
             AND source_column.attnum = constraint_row.conkey[1]
            JOIN pg_attribute target_column
              ON target_column.attrelid = constraint_row.confrelid
             AND target_column.attnum = constraint_row.confkey[1]
            WHERE constraint_row.conrelid = correction.table_oid
              AND constraint_row.conname = correction.constraint_name
              AND constraint_row.contype = 'f'
              AND constraint_row.convalidated
              AND constraint_row.conkey = ARRAY[client_user_attnum]::smallint[]
              AND source_column.attname = 'client_user_id'
              AND constraint_row.confrelid = 'internal.support_users'::regclass
              AND constraint_row.confkey = ARRAY[support_user_id_attnum]::smallint[]
              AND target_column.attname = 'id'
        ) THEN
            RAISE EXCEPTION
                'Canonical validated client_user_id foreign key was not restored on %',
                correction.table_oid::regclass;
        END IF;
    END LOOP;

    -- A rerun may find the canonical integer column but a missing tenant trigger.
    -- Fresh deployments reach this block before the function is created, so the
    -- normal fresh-schema trigger creation below remains the single creator there.
    IF attachment_trigger_function_oid IS NOT NULL
       AND NOT EXISTS (
           SELECT 1
           FROM pg_trigger trigger_row
           WHERE trigger_row.tgrelid = 'digital.attachments'::regclass
             AND trigger_row.tgname = 'trg_attachments_client_uploader_tenant'
             AND NOT trigger_row.tgisinternal
       ) THEN
        IF attachment_table_owner <> current_user
           AND NOT pg_has_role(current_user, attachment_table_owner, 'USAGE')
           AND NOT (SELECT rolsuper FROM pg_roles WHERE rolname = current_user) THEN
            RAISE EXCEPTION
                'CREATE TRIGGER requires owner of digital.attachments (owner %, executor %)',
                attachment_table_owner,
                current_user;
        END IF;

        CREATE TRIGGER trg_attachments_client_uploader_tenant
        BEFORE INSERT OR UPDATE OF client_user_id, client_id
        ON digital.attachments
        FOR EACH ROW
        EXECUTE FUNCTION digital.enforce_attachment_client_uploader_tenant();
    END IF;

    IF attachment_trigger_was_dropped
       AND NOT EXISTS (
           SELECT 1
           FROM pg_trigger trigger_row
           WHERE trigger_row.tgrelid = 'digital.attachments'::regclass
             AND trigger_row.tgname = 'trg_attachments_client_uploader_tenant'
             AND NOT trigger_row.tgisinternal
             AND trigger_row.tgfoid = attachment_trigger_function_oid
             AND trigger_row.tgtype = 23
             AND trigger_row.tgattr::text = format(
                 '%s %s', attachment_client_user_attnum, attachment_client_id_attnum)
             AND trigger_row.tgenabled = 'O'
             AND trigger_row.tgnargs = 0
       ) THEN
        RAISE EXCEPTION
            'Canonical attachment tenant trigger was not restored after client_user_id conversion';
    END IF;
END
$client_user_identity_columns$;

-- A historical restore can preserve the valid primary-key index while assigning
-- PostgreSQL's default <table>_pkey constraint name. The repository contract is
-- name-strict, so normalize the equivalent quota key without rebuilding it.
DO $attachment_tenant_quota_primary_key$
DECLARE
    client_id_attnum smallint;
    primary_key_count integer;
    unexpected_primary_keys text;
    quota_owner name;
    named_constraint record;
    alternative_primary_key record;
    required_key record;
    alternative_named_keys text;
BEGIN
    SELECT column_row.attnum,
           owner_role.rolname
    INTO client_id_attnum,
         quota_owner
    FROM pg_attribute column_row
    JOIN pg_class quota_table
      ON quota_table.oid = column_row.attrelid
    JOIN pg_roles owner_role
      ON owner_role.oid = quota_table.relowner
    WHERE column_row.attrelid = 'digital.attachment_tenant_quotas'::regclass
      AND column_row.attname = 'client_id'
      AND column_row.attnum > 0
      AND NOT column_row.attisdropped;

    SELECT count(*)
    INTO primary_key_count
    FROM pg_constraint constraint_row
    WHERE constraint_row.conrelid = 'digital.attachment_tenant_quotas'::regclass
      AND constraint_row.contype = 'p';

    IF primary_key_count > 1 THEN
        RAISE EXCEPTION
            'digital.attachment_tenant_quotas has multiple primary-key constraints; refusing name repair';
    END IF;

    SELECT constraint_row.*,
           index_state.indisprimary,
           index_state.indisunique,
           index_state.indisvalid,
           index_state.indislive,
           index_state.indisready
    INTO named_constraint
    FROM pg_constraint constraint_row
    LEFT JOIN pg_index index_state
      ON index_state.indexrelid = constraint_row.conindid
    WHERE constraint_row.conrelid = 'digital.attachment_tenant_quotas'::regclass
      AND constraint_row.conname = 'pk_attachment_tenant_quotas';

    IF FOUND THEN
        IF named_constraint.contype <> 'p'
           OR named_constraint.conkey <> ARRAY[client_id_attnum]::smallint[]
           OR NOT named_constraint.convalidated
           OR named_constraint.condeferrable
           OR named_constraint.condeferred
           OR NOT named_constraint.indisprimary
           OR NOT named_constraint.indisunique
           OR NOT named_constraint.indisvalid
           OR NOT named_constraint.indislive
           OR NOT named_constraint.indisready THEN
            RAISE EXCEPTION
                'Conflicting constraint named pk_attachment_tenant_quotas is not a validated primary key on (client_id) with a valid primary index';
        END IF;
    ELSIF primary_key_count = 1 THEN
        SELECT constraint_row.*,
               index_state.indisprimary,
               index_state.indisunique,
               index_state.indisvalid,
               index_state.indislive,
               index_state.indisready
        INTO alternative_primary_key
        FROM pg_constraint constraint_row
        JOIN pg_index index_state
          ON index_state.indexrelid = constraint_row.conindid
        WHERE constraint_row.conrelid = 'digital.attachment_tenant_quotas'::regclass
          AND constraint_row.contype = 'p';

        IF alternative_primary_key.conkey <> ARRAY[client_id_attnum]::smallint[] THEN
            RAISE EXCEPTION
                'digital.attachment_tenant_quotas primary key % is on different columns; expected (client_id)',
                alternative_primary_key.conname;
        END IF;

        IF NOT alternative_primary_key.convalidated
           OR alternative_primary_key.condeferrable
           OR alternative_primary_key.condeferred
           OR NOT alternative_primary_key.indisprimary
           OR NOT alternative_primary_key.indisunique
           OR NOT alternative_primary_key.indisvalid
           OR NOT alternative_primary_key.indislive
           OR NOT alternative_primary_key.indisready THEN
            RAISE EXCEPTION
                'digital.attachment_tenant_quotas primary key % has an invalid or unusable supporting index',
                alternative_primary_key.conname;
        END IF;

        IF quota_owner <> current_user
           AND NOT pg_has_role(current_user, quota_owner, 'USAGE')
           AND NOT (SELECT rolsuper FROM pg_roles WHERE rolname = current_user) THEN
            RAISE EXCEPTION
                'ALTER TABLE requires owner of digital.attachment_tenant_quotas (owner %, executor %)',
                quota_owner,
                current_user;
        END IF;

        EXECUTE format(
            'ALTER TABLE digital.attachment_tenant_quotas RENAME CONSTRAINT %I TO pk_attachment_tenant_quotas',
            alternative_primary_key.conname);
    ELSE
        IF EXISTS (
            SELECT 1
            FROM digital.attachment_tenant_quotas
            WHERE client_id IS NULL
        ) THEN
            RAISE EXCEPTION
                'Cannot add pk_attachment_tenant_quotas: client_id contains NULL values';
        END IF;

        IF EXISTS (
            SELECT client_id
            FROM digital.attachment_tenant_quotas
            GROUP BY client_id
            HAVING count(*) > 1
        ) THEN
            RAISE EXCEPTION
                'Cannot add pk_attachment_tenant_quotas: client_id contains duplicate values';
        END IF;

        IF quota_owner <> current_user
           AND NOT pg_has_role(current_user, quota_owner, 'USAGE')
           AND NOT (SELECT rolsuper FROM pg_roles WHERE rolname = current_user) THEN
            RAISE EXCEPTION
                'ALTER TABLE requires owner of digital.attachment_tenant_quotas (owner %, executor %)',
                quota_owner,
                current_user;
        END IF;

        ALTER TABLE digital.attachment_tenant_quotas
            ADD CONSTRAINT pk_attachment_tenant_quotas PRIMARY KEY (client_id);
    END IF;

    -- Every explicitly named messaging primary/unique key is audited here. This
    -- does not silently rename unrelated restored constraints: an equivalent key
    -- under a generated name is reported before final validation, where the exact
    -- repository contract is also enforced.
    FOR required_key IN
        SELECT *
        FROM (VALUES
            ('digital.instructions'::regclass, 'uq_instructions_id_conversation_client', 'u', ARRAY['id','instruction_id','client_id']::text[]),
            ('digital.conversation_access'::regclass, 'pk_conversation_access', 'p', ARRAY['conversation_id']::text[]),
            ('digital.conversation_access'::regclass, 'uq_conversation_access_id_client', 'u', ARRAY['conversation_id','client_id']::text[]),
            ('digital.conversation_sequences'::regclass, 'pk_conversation_sequences', 'p', ARRAY['conversation_id']::text[]),
            ('digital.conversation_read_cursors'::regclass, 'pk_conversation_read_cursors', 'p', ARRAY['read_cursor_id']::text[]),
            ('digital.conversation_outbox'::regclass, 'pk_conversation_outbox', 'p', ARRAY['event_id']::text[]),
            ('digital.conversation_audit'::regclass, 'pk_conversation_audit', 'p', ARRAY['audit_id']::text[]),
            ('digital.attachments'::regclass, 'pk_attachments', 'p', ARRAY['id']::text[]),
            ('digital.attachments'::regclass, 'uq_attachments_id_client', 'u', ARRAY['id','client_id']::text[]),
            ('digital.attachment_audit'::regclass, 'pk_attachment_audit', 'p', ARRAY['audit_id']::text[])
        ) AS required(table_oid, constraint_name, constraint_type, key_names)
    LOOP
        SELECT string_agg(constraint_row.conname, ', ' ORDER BY constraint_row.conname)
        INTO alternative_named_keys
        FROM pg_constraint constraint_row
        WHERE constraint_row.conrelid = required_key.table_oid
          AND constraint_row.contype = required_key.constraint_type
          AND constraint_row.conname <> required_key.constraint_name
          AND constraint_row.conkey = ARRAY(
              SELECT column_row.attnum
              FROM unnest(required_key.key_names) WITH ORDINALITY AS key_name(attname, ordinality)
              JOIN pg_attribute column_row
                ON column_row.attrelid = required_key.table_oid
               AND column_row.attname = key_name.attname
               AND column_row.attnum > 0
               AND NOT column_row.attisdropped
              ORDER BY key_name.ordinality
          )::smallint[];

        IF alternative_named_keys IS NOT NULL
           AND NOT EXISTS (
               SELECT 1
               FROM pg_constraint constraint_row
               WHERE constraint_row.conrelid = required_key.table_oid
                 AND constraint_row.conname = required_key.constraint_name
           ) THEN
            RAISE EXCEPTION
                'Equivalent % constraint(s) % found on % but required name % is absent; manual review is required',
                required_key.constraint_type,
                alternative_named_keys,
                required_key.table_oid::regclass,
                required_key.constraint_name;
        END IF;
    END LOOP;
END
$attachment_tenant_quota_primary_key$;

-- Normalize only the reviewed legacy sentinel classification. No row is removed
-- or replaced, and message text/identifiers/timestamps are untouched.
UPDATE digital.instructions message
SET inst_type_id = root.inst_type_id,
    inst_category_id = root.inst_category_id
FROM digital.instructions root
WHERE (SELECT deploy_required FROM manual_deployment_state)
  AND message.id <> root.id
  AND root.id = message.instruction_id
  AND root.instruction_id = root.id
  AND (
      (root.inst_type_id BETWEEN 110 AND 117 AND root.inst_category_id = 101)
      OR (root.inst_type_id IN (121,122) AND root.inst_category_id = 102)
  )
  AND message.client_id IS NOT DISTINCT FROM root.client_id
  AND (
      message.inst_type_id IS DISTINCT FROM root.inst_type_id
      OR message.inst_category_id IS DISTINCT FROM root.inst_category_id
  )
  AND message.inst_type_id IN (100, root.inst_type_id)
  AND message.inst_category_id IN (100, root.inst_category_id);

CREATE TEMPORARY TABLE manual_sequence_assignment (
    instruction_record_id bigint PRIMARY KEY,
    assigned_sequence bigint NOT NULL
) ON COMMIT PRESERVE ROWS;

INSERT INTO manual_sequence_assignment (instruction_record_id, assigned_sequence)
SELECT root.id,
       CASE
           WHEN (root.inst_type_id BETWEEN 110 AND 117 AND root.inst_category_id = 101)
                OR (root.inst_type_id IN (121,122) AND root.inst_category_id = 102)
               THEN 1::bigint
           WHEN NULLIF(btrim(root.instruction), '') IS NULL THEN 0::bigint
           ELSE 1::bigint
       END
FROM digital.instructions root
WHERE (SELECT deploy_required FROM manual_deployment_state)
  AND root.instruction_id = root.id
UNION ALL
SELECT message.id,
       row_number() OVER (
           PARTITION BY message.instruction_id
           ORDER BY COALESCE(message.datetime, message.insert_date), message.id
       )
       + CASE
           WHEN (root.inst_type_id BETWEEN 110 AND 117 AND root.inst_category_id = 101)
                OR (root.inst_type_id IN (121,122) AND root.inst_category_id = 102)
               THEN 1
           WHEN NULLIF(btrim(root.instruction), '') IS NULL THEN 0
           ELSE 1
         END
FROM digital.instructions message
JOIN digital.instructions root ON root.id = message.instruction_id
WHERE (SELECT deploy_required FROM manual_deployment_state)
  AND message.id <> root.id;

UPDATE digital.instructions instruction
SET conversation_sequence = assignment.assigned_sequence
FROM manual_sequence_assignment assignment
WHERE instruction.id = assignment.instruction_record_id;

UPDATE digital.instructions
SET insert_user = NULL
WHERE (SELECT deploy_required FROM manual_deployment_state)
  AND client_auth_user_id IS NOT NULL
  AND insert_user IS NOT NULL;

INSERT INTO digital.conversation_access (
    conversation_id, client_id, conversation_kind, state,
    client_user_id, admin_user_id, version, created_at, archived_at)
SELECT root.id,
       root.client_id,
       CASE
           WHEN root.inst_type_id = 100 THEN 'Group'
           WHEN root.inst_type_id = 101 THEN 'Private'
           WHEN root.inst_category_id = 101 THEN 'Ticket'
           ELSE 'Inquiry'
       END,
       CASE WHEN root.inst_type_id = 101 THEN 'NeedsReview' ELSE 'Active' END,
       NULL,
       NULL,
       1,
       COALESCE(root.datetime, root.insert_date),
       NULL
FROM digital.instructions root
WHERE (SELECT deploy_required FROM manual_deployment_state)
  AND root.instruction_id = root.id
  AND (
      root.inst_type_id IN (100,101)
      OR (root.inst_type_id BETWEEN 110 AND 117 AND root.inst_category_id = 101)
      OR (root.inst_type_id IN (121,122) AND root.inst_category_id = 102)
  );

INSERT INTO digital.conversation_sequences (conversation_id, next_sequence)
SELECT root.id,
       COALESCE(max(message.conversation_sequence) + 1, 1)
FROM digital.instructions root
LEFT JOIN digital.instructions message ON message.instruction_id = root.id
WHERE (SELECT deploy_required FROM manual_deployment_state)
  AND root.instruction_id = root.id
GROUP BY root.id;

INSERT INTO digital.conversation_audit (
    conversation_id, client_id, action, actor_kind, occurred_at, details)
SELECT access.conversation_id,
       access.client_id,
       CASE WHEN access.conversation_kind IN ('Ticket','Inquiry')
            THEN 'CaseHistorySequenced'
            ELSE 'LegacyAccessBackfilled'
       END,
       'System',
       access.created_at,
       jsonb_build_object(
           'conversationKind', access.conversation_kind,
           'initialState', access.state,
           'manualDeployment', '20260803_messaging_attachments_test')
FROM digital.conversation_access access
WHERE (SELECT deploy_required FROM manual_deployment_state);

DO $instruction_constraints$
BEGIN
    IF NOT (SELECT deploy_required FROM manual_deployment_state) THEN
        RETURN;
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conrelid = 'digital.instructions'::regclass
          AND conname = 'ck_instructions_client_author_exclusive'
    ) THEN
        ALTER TABLE digital.instructions
            ADD CONSTRAINT ck_instructions_client_author_exclusive
            CHECK (client_auth_user_id IS NULL OR insert_user IS NULL);
    END IF;

    ALTER TABLE digital.instructions
        ADD CONSTRAINT ck_instructions_conversation_sequence_shape CHECK (
            (inst_type_id = 105 AND client_message_id IS NULL)
            OR
            (instruction_id IS NULL
                AND conversation_sequence IS NULL
                AND client_message_id IS NULL)
            OR
            (instruction_id IS NULL
                AND conversation_sequence = 0
                AND client_message_id IS NULL
                AND NULLIF(btrim(instruction), '') IS NULL)
            OR
            (instruction_id IS NULL
                AND conversation_sequence = 1
                AND client_message_id IS NULL
                AND inst_type_id IN (110,111,112,113,114,115,116,117,121,122)
                AND NULLIF(btrim(instruction), '') IS NOT NULL)
            OR
            (instruction_id = id
                AND client_message_id IS NULL
                AND (
                    (conversation_sequence = 0 AND NULLIF(btrim(instruction), '') IS NULL)
                    OR (conversation_sequence > 0 AND NULLIF(btrim(instruction), '') IS NOT NULL)
                ))
            OR
            (instruction_id <> id
                AND conversation_sequence > 0
                AND (
                    NULLIF(btrim(instruction), '') IS NOT NULL
                    OR client_message_id IS NOT NULL
                ))
        );

    COMMENT ON COLUMN digital.instructions.client_auth_user_id IS
        'Canonical Client author. References internal.support_users(id); NULL for Admin-authored rows.';
    COMMENT ON COLUMN digital.instructions.insert_user IS
        'Canonical Admin author/audit user. References admin.users(id); NULL for Client-authored rows.';
END
$instruction_constraints$;

DO $create_indexes$
BEGIN
    IF NOT (SELECT deploy_required FROM manual_deployment_state) THEN
        RETURN;
    END IF;

    CREATE UNIQUE INDEX ix_instructions_conversation_sequence_unique
        ON digital.instructions (instruction_id, conversation_sequence)
        WHERE instruction_id IS NOT NULL;
    CREATE UNIQUE INDEX ix_instructions_client_message_unique
        ON digital.instructions (client_message_id)
        WHERE client_message_id IS NOT NULL;
    CREATE UNIQUE INDEX ix_conversation_access_group_unique
        ON digital.conversation_access (client_id)
        WHERE conversation_kind = 'Group';
    CREATE UNIQUE INDEX ix_conversation_access_active_private_pair_unique
        ON digital.conversation_access (client_id, client_user_id, admin_user_id)
        WHERE conversation_kind = 'Private' AND state = 'Active';
    CREATE UNIQUE INDEX ix_conversation_read_cursors_admin_unique
        ON digital.conversation_read_cursors (conversation_id, admin_user_id)
        WHERE principal_kind = 'Admin';
    CREATE UNIQUE INDEX ix_conversation_read_cursors_client_unique
        ON digital.conversation_read_cursors (conversation_id, client_user_id)
        WHERE principal_kind = 'Client';
    CREATE INDEX ix_conversation_access_client_state_kind
        ON digital.conversation_access (client_id, state, conversation_kind, conversation_id);
    CREATE INDEX ix_conversation_outbox_dispatch
        ON digital.conversation_outbox (available_at, event_id)
        WHERE processed_at IS NULL AND dead_lettered_at IS NULL;
    CREATE INDEX ix_conversation_audit_conversation_occurred
        ON digital.conversation_audit (conversation_id, occurred_at DESC, audit_id DESC);
    CREATE INDEX ix_conversation_audit_client_occurred
        ON digital.conversation_audit (client_id, occurred_at DESC, audit_id DESC);

    CREATE UNIQUE INDEX uq_attachments_message_position
        ON digital.attachments (message_id, position)
        WHERE message_id IS NOT NULL;
    CREATE UNIQUE INDEX uq_attachments_quarantine_key
        ON digital.attachments (quarantine_key)
        WHERE quarantine_key IS NOT NULL;
    CREATE UNIQUE INDEX uq_attachments_ready_key
        ON digital.attachments (ready_key)
        WHERE ready_key IS NOT NULL;
    CREATE INDEX ix_attachments_conversation_state
        ON digital.attachments (conversation_id, state, created_at);
    CREATE INDEX ix_attachments_client_active_storage
        ON digital.attachments (client_id, state)
        WHERE state IN (
            'PendingUpload','Uploaded','StructuralValidation','StructurallyValidated',
            'Scanning','Promoting','Ready','DeletePending');
    CREATE INDEX ix_attachments_user_rolling_quota
        ON digital.attachments (client_id, client_user_id, admin_user_id, created_at DESC);
    CREATE INDEX ix_attachments_scan_claim
        ON digital.attachments (next_attempt_at, created_at)
        WHERE state IN (
            'Uploaded','StructuralValidation','StructurallyValidated','Scanning','Promoting');
    CREATE INDEX ix_attachments_cleanup_claim
        ON digital.attachments (state, expires_at, created_at)
        WHERE state IN ('PendingUpload','Ready','DeletePending');
    CREATE INDEX ix_attachments_ready_quarantine_cleanup
        ON digital.attachments (next_attempt_at, ready_at, id)
        WHERE state = 'Ready' AND quarantine_key IS NOT NULL;
    CREATE INDEX ix_attachment_audit_attachment_time
        ON digital.attachment_audit (attachment_id, occurred_at, audit_id);
END
$create_indexes$;

DO $create_functions_and_triggers$
BEGIN
    IF NOT (SELECT deploy_required FROM manual_deployment_state) THEN
        RETURN;
    END IF;

    CREATE FUNCTION digital.maintain_attachment_quota_reservation()
    RETURNS trigger
    LANGUAGE plpgsql
    AS $function_body$
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
    $function_body$;

    CREATE TRIGGER trg_attachments_quota_reservation
    BEFORE INSERT OR UPDATE OF state, declared_size, actual_size, reservation_bytes
    ON digital.attachments
    FOR EACH ROW
    EXECUTE FUNCTION digital.maintain_attachment_quota_reservation();

    CREATE FUNCTION digital.enforce_attachment_client_uploader_tenant()
    RETURNS trigger
    LANGUAGE plpgsql
    AS $function_body$
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
    $function_body$;

    CREATE TRIGGER trg_attachments_client_uploader_tenant
    BEFORE INSERT OR UPDATE OF client_user_id, client_id
    ON digital.attachments
    FOR EACH ROW
    EXECUTE FUNCTION digital.enforce_attachment_client_uploader_tenant();
END
$create_functions_and_triggers$;

DO $grants$
DECLARE
    grant_item record;
    qualified_table regclass;
    qualified_sequence regclass;
    qualified_function regprocedure;
BEGIN
    -- The table owner grants only the operations used by the current repositories.
    FOR grant_item IN
        SELECT *
        FROM (VALUES
            ('digital.instructions', 'SELECT, INSERT, UPDATE'),
            ('digital.conversation_access', 'SELECT, INSERT, UPDATE'),
            ('digital.conversation_sequences', 'SELECT, INSERT, UPDATE'),
            ('digital.conversation_read_cursors', 'SELECT, INSERT, UPDATE'),
            ('digital.conversation_outbox', 'SELECT, INSERT, UPDATE'),
            ('digital.conversation_audit', 'SELECT, INSERT'),
            ('digital.attachment_tenant_quotas', 'SELECT'),
            ('digital.attachments', 'SELECT, INSERT, UPDATE'),
            ('digital.attachment_audit', 'SELECT, INSERT')
        ) AS permission(qualified_name, operations)
    LOOP
        qualified_table := to_regclass(grant_item.qualified_name);
        IF qualified_table IS NULL THEN
            RAISE EXCEPTION
                'Partial deployment detected: required table % is missing before grant application',
                grant_item.qualified_name;
        END IF;

        IF (SELECT relowner FROM pg_class WHERE oid = qualified_table)
           = (SELECT oid FROM pg_roles WHERE rolname = current_user) THEN
            EXECUTE format(
                'GRANT %s ON TABLE %s TO %I',
                grant_item.operations,
                qualified_table,
                'shovan');
        ELSIF NOT (
            SELECT bool_and(
                has_table_privilege('shovan', grant_item.qualified_name, required_privilege))
            FROM regexp_split_to_table(grant_item.operations, E',\\s*')
                 AS privileges(required_privilege)
        ) THEN
            RAISE EXCEPTION
                'Role shovan lacks % on %, and executor % is not the table owner',
                grant_item.operations,
                grant_item.qualified_name,
                current_user;
        END IF;
    END LOOP;

    FOR grant_item IN
        SELECT qualified_name
        FROM (
            VALUES
                ('digital.conversation_read_cursors_read_cursor_id_seq'),
                ('digital.conversation_audit_audit_id_seq'),
                ('digital.attachment_audit_audit_id_seq')
            UNION ALL
            SELECT pg_get_serial_sequence('digital.instructions', 'id')
        ) AS sequence_name(qualified_name)
        WHERE qualified_name IS NOT NULL
    LOOP
        qualified_sequence := to_regclass(grant_item.qualified_name);
        IF qualified_sequence IS NULL THEN
            RAISE EXCEPTION
                'Partial deployment detected: required sequence % is missing before grant application',
                grant_item.qualified_name;
        END IF;

        IF (SELECT relowner FROM pg_class WHERE oid = qualified_sequence)
           = (SELECT oid FROM pg_roles WHERE rolname = current_user) THEN
            EXECUTE format(
                'GRANT USAGE ON SEQUENCE %s TO %I',
                qualified_sequence,
                'shovan');
        ELSIF NOT has_sequence_privilege('shovan', qualified_sequence, 'USAGE') THEN
            RAISE EXCEPTION
                'Role shovan lacks USAGE on sequence %, and executor % is not its owner',
                grant_item.qualified_name,
                current_user;
        END IF;
    END LOOP;

    FOR grant_item IN
        SELECT function_name
        FROM (VALUES
            ('digital.maintain_attachment_quota_reservation()'),
            ('digital.enforce_attachment_client_uploader_tenant()')
        ) AS required_function(function_name)
    LOOP
        qualified_function := to_regprocedure(grant_item.function_name);
        IF qualified_function IS NULL THEN
            RAISE EXCEPTION
                'Partial deployment detected: required function % is missing before grant application',
                grant_item.function_name;
        END IF;

        IF (SELECT proowner FROM pg_proc WHERE oid = qualified_function)
           = (SELECT oid FROM pg_roles WHERE rolname = current_user) THEN
            EXECUTE format(
                'GRANT EXECUTE ON FUNCTION %s TO %I',
                qualified_function,
                'shovan');
        ELSIF NOT has_function_privilege(
            'shovan', qualified_function, 'EXECUTE') THEN
            RAISE EXCEPTION
                'Role shovan lacks EXECUTE on function %, and executor % is not its owner',
                grant_item.function_name,
                current_user;
        END IF;
    END LOOP;

    IF NOT has_schema_privilege('shovan', 'digital', 'USAGE') THEN
        RAISE EXCEPTION 'Role shovan requires USAGE on schema digital';
    END IF;
END
$grants$;

DO $post_deployment_validation$
DECLARE
    missing_name text;
BEGIN
    SELECT required_name
    INTO missing_name
    FROM unnest(ARRAY[
        'digital.conversation_access',
        'digital.conversation_sequences',
        'digital.conversation_read_cursors',
        'digital.conversation_outbox',
        'digital.conversation_audit',
        'digital.attachment_tenant_quotas',
        'digital.attachments',
        'digital.attachment_audit'
    ]) AS required(required_name)
    WHERE to_regclass(required_name) IS NULL
    LIMIT 1;

    IF missing_name IS NOT NULL THEN
        RAISE EXCEPTION 'Post-deployment validation: missing table %', missing_name;
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM information_schema.columns
        WHERE table_schema = 'digital' AND table_name = 'instructions'
          AND column_name = 'client_message_id' AND data_type = 'uuid'
          AND is_nullable = 'YES'
    ) OR NOT EXISTS (
        SELECT 1 FROM information_schema.columns
        WHERE table_schema = 'digital' AND table_name = 'instructions'
          AND column_name = 'conversation_sequence' AND data_type = 'bigint'
          AND is_nullable = 'YES'
    ) OR NOT EXISTS (
        SELECT 1 FROM information_schema.columns
        WHERE table_schema = 'digital' AND table_name = 'instructions'
          AND column_name = 'client_auth_user_id' AND data_type = 'integer'
          AND is_nullable = 'YES'
    ) OR NOT EXISTS (
        SELECT 1 FROM information_schema.columns
        WHERE table_schema = 'digital' AND table_name = 'instructions'
          AND column_name = 'insert_user' AND data_type = 'integer'
          AND is_nullable = 'YES'
    ) THEN
        RAISE EXCEPTION
            'Post-deployment validation: instruction messaging columns are missing or incompatible';
    END IF;

    IF EXISTS (
        WITH expected(table_name) AS (
            VALUES
                ('conversation_access'),
                ('conversation_read_cursors'),
                ('conversation_outbox'),
                ('conversation_audit'),
                ('attachments'),
                ('attachment_audit')
        )
        SELECT 1
        FROM expected
        LEFT JOIN pg_class table_row
          ON table_row.relnamespace = 'digital'::regnamespace
         AND table_row.relname = expected.table_name
         AND table_row.relkind IN ('r', 'p')
        LEFT JOIN pg_attribute column_row
          ON column_row.attrelid = table_row.oid
         AND column_row.attname = 'client_user_id'
         AND column_row.attnum > 0
         AND NOT column_row.attisdropped
        WHERE column_row.attname IS NULL
           OR format_type(column_row.atttypid, column_row.atttypmod) <> 'integer'
    ) THEN
        RAISE EXCEPTION
            'Post-deployment validation: every Messaging V2 and attachment client_user_id column must be integer';
    END IF;

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
            'Post-deployment validation: canonical client identity foreign key is missing, unvalidated, or targets the wrong table/columns';
    END IF;

    SELECT expected.constraint_name
    INTO missing_name
    FROM unnest(ARRAY[
        'ck_instructions_client_author_exclusive',
        'uq_instructions_id_conversation_client',
        'ck_instructions_conversation_sequence_shape',
        'pk_conversation_access',
        'uq_conversation_access_id_client',
        'pk_conversation_sequences',
        'pk_conversation_read_cursors',
        'pk_conversation_outbox',
        'pk_conversation_audit',
        'pk_attachment_tenant_quotas',
        'pk_attachments',
        'uq_attachments_id_client',
        'pk_attachment_audit',
        'fk_attachments_conversation_client',
        'fk_attachments_message_conversation_client',
        'fk_attachment_audit_attachment_client',
        'ck_attachments_state',
        'ck_attachments_sizes',
        'ck_attachments_ready_shape',
        'ck_attachments_bound_retention',
        'ck_attachments_ready_unbound_retention'
    ]) AS expected(constraint_name)
    WHERE NOT EXISTS (
        SELECT 1
        FROM pg_constraint actual
        WHERE actual.conname = expected.constraint_name
          AND actual.convalidated
          AND actual.connamespace = 'digital'::regnamespace
    )
    LIMIT 1;

    IF missing_name IS NOT NULL THEN
        RAISE EXCEPTION
            'Post-deployment validation: missing or unvalidated constraint %',
            missing_name;
    END IF;

    SELECT expected.index_name
    INTO missing_name
    FROM unnest(ARRAY[
        'ix_instructions_conversation_sequence_unique',
        'ix_instructions_client_message_unique',
        'ix_conversation_access_group_unique',
        'ix_conversation_access_active_private_pair_unique',
        'ix_conversation_read_cursors_admin_unique',
        'ix_conversation_read_cursors_client_unique',
        'ix_conversation_access_client_state_kind',
        'ix_conversation_outbox_dispatch',
        'ix_conversation_audit_conversation_occurred',
        'ix_conversation_audit_client_occurred',
        'uq_attachments_message_position',
        'uq_attachments_quarantine_key',
        'uq_attachments_ready_key',
        'ix_attachments_conversation_state',
        'ix_attachments_client_active_storage',
        'ix_attachments_user_rolling_quota',
        'ix_attachments_scan_claim',
        'ix_attachments_cleanup_claim',
        'ix_attachments_ready_quarantine_cleanup',
        'ix_attachment_audit_attachment_time'
    ]) AS expected(index_name)
    WHERE NOT EXISTS (
        SELECT 1
        FROM pg_class index_class
        JOIN pg_index index_state ON index_state.indexrelid = index_class.oid
        WHERE index_class.relnamespace = 'digital'::regnamespace
          AND index_class.relname = expected.index_name
          AND index_state.indisvalid
          AND index_state.indisready
    )
    LIMIT 1;

    IF missing_name IS NOT NULL THEN
        RAISE EXCEPTION 'Post-deployment validation: missing or invalid index %', missing_name;
    END IF;

    IF to_regprocedure('digital.maintain_attachment_quota_reservation()') IS NULL
       OR to_regprocedure('digital.enforce_attachment_client_uploader_tenant()') IS NULL THEN
        RAISE EXCEPTION 'Post-deployment validation: attachment trigger function missing';
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM pg_trigger
        WHERE tgrelid = 'digital.attachments'::regclass
          AND tgname = 'trg_attachments_quota_reservation'
          AND NOT tgisinternal
          AND tgenabled <> 'D'
    ) THEN
        RAISE EXCEPTION 'Post-deployment validation: attachment trigger missing or disabled';
    END IF;

    IF NOT EXISTS (
        SELECT 1
        FROM pg_trigger trigger_row
        WHERE trigger_row.tgrelid = 'digital.attachments'::regclass
          AND trigger_row.tgname = 'trg_attachments_client_uploader_tenant'
          AND trigger_row.tgfoid = 'digital.enforce_attachment_client_uploader_tenant()'::regprocedure
          AND trigger_row.tgtype = 23
          AND trigger_row.tgattr::text = array_to_string(ARRAY[
              (SELECT attnum
               FROM pg_attribute
               WHERE attrelid = 'digital.attachments'::regclass
                 AND attname = 'client_user_id'
                 AND attnum > 0
                 AND NOT attisdropped),
              (SELECT attnum
               FROM pg_attribute
               WHERE attrelid = 'digital.attachments'::regclass
                 AND attname = 'client_id'
                 AND attnum > 0
                 AND NOT attisdropped)
          ]::smallint[], ' ')
          AND trigger_row.tgenabled = 'O'
          AND NOT trigger_row.tgisinternal
          AND trigger_row.tgnargs = 0
    ) THEN
        RAISE EXCEPTION
            'Post-deployment validation: trg_attachments_client_uploader_tenant definition is missing, disabled, or incompatible';
    END IF;

    IF EXISTS (
        SELECT instruction_id, conversation_sequence
        FROM digital.instructions
        WHERE instruction_id IS NOT NULL
        GROUP BY instruction_id, conversation_sequence
        HAVING count(*) > 1
    ) THEN
        RAISE EXCEPTION 'Post-deployment validation: duplicate conversation sequence';
    END IF;

    IF EXISTS (
        SELECT 1
        FROM digital.conversation_sequences allocator
        LEFT JOIN digital.instructions message
          ON message.instruction_id = allocator.conversation_id
        GROUP BY allocator.conversation_id, allocator.next_sequence
        HAVING allocator.next_sequence <= COALESCE(max(message.conversation_sequence), 0)
    ) THEN
        RAISE EXCEPTION 'Post-deployment validation: allocator is not above history';
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
                WHEN root.inst_category_id = 101
                     AND root.inst_type_id BETWEEN 110 AND 117 THEN 'Ticket'
                WHEN root.inst_category_id = 102
                     AND root.inst_type_id IN (121,122) THEN 'Inquiry'
                ELSE NULL
              END
    ) THEN
        RAISE EXCEPTION 'Post-deployment validation: access/root mapping mismatch';
    END IF;

    IF EXISTS (
        SELECT 1
        FROM digital.instructions instruction
        LEFT JOIN internal.support_users support_user
          ON support_user.id = instruction.client_auth_user_id
        WHERE instruction.client_auth_user_id IS NOT NULL
          AND (instruction.insert_user IS NOT NULL
               OR support_user.id IS NULL
               OR support_user.client_id IS DISTINCT FROM instruction.client_id)
    ) THEN
        RAISE EXCEPTION 'Post-deployment validation: instruction client-author tenant mismatch';
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
        RAISE EXCEPTION 'Post-deployment validation: conversation client-user tenant mismatch';
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
        RAISE EXCEPTION 'Post-deployment validation: read-cursor client-user tenant mismatch';
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
        RAISE EXCEPTION 'Post-deployment validation: outbox client-user tenant mismatch';
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
        RAISE EXCEPTION 'Post-deployment validation: conversation audit client-user tenant mismatch';
    END IF;

    IF EXISTS (
        SELECT 1
        FROM digital.attachments attachment
        JOIN digital.conversation_access access
          ON access.conversation_id = attachment.conversation_id
        LEFT JOIN internal.support_users support_user
          ON support_user.id = attachment.client_user_id
        WHERE attachment.client_user_id IS NOT NULL
          AND (attachment.client_id IS DISTINCT FROM access.client_id
               OR support_user.id IS NULL
               OR support_user.client_id IS DISTINCT FROM attachment.client_id)
    ) THEN
        RAISE EXCEPTION 'Post-deployment validation: attachment client-user tenant mismatch';
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
        RAISE EXCEPTION 'Post-deployment validation: attachment audit client-user tenant mismatch';
    END IF;

    IF NOT has_table_privilege('shovan', 'digital.instructions', 'SELECT')
       OR NOT has_table_privilege('shovan', 'digital.instructions', 'INSERT')
       OR NOT has_table_privilege('shovan', 'digital.instructions', 'UPDATE')
       OR NOT has_table_privilege('shovan', 'digital.conversation_access', 'SELECT')
       OR NOT has_table_privilege('shovan', 'digital.conversation_access', 'INSERT')
       OR NOT has_table_privilege('shovan', 'digital.conversation_access', 'UPDATE')
       OR NOT has_table_privilege('shovan', 'digital.attachments', 'SELECT')
       OR NOT has_table_privilege('shovan', 'digital.attachments', 'INSERT')
       OR NOT has_table_privilege('shovan', 'digital.attachments', 'UPDATE')
       OR NOT has_table_privilege('shovan', 'digital.attachment_audit', 'SELECT')
       OR NOT has_table_privilege('shovan', 'digital.attachment_audit', 'INSERT')
       OR NOT has_table_privilege('shovan', 'admin.users', 'SELECT')
       OR NOT has_table_privilege('shovan', 'internal.support_users', 'SELECT')
       OR NOT has_table_privilege('shovan', 'internal.clients', 'SELECT') THEN
        RAISE EXCEPTION 'Post-deployment validation: role shovan lacks application DML privileges';
    END IF;

    IF NOT has_sequence_privilege('shovan', 'digital.conversation_read_cursors_read_cursor_id_seq', 'USAGE')
       OR NOT has_sequence_privilege('shovan', 'digital.conversation_audit_audit_id_seq', 'USAGE')
       OR NOT has_sequence_privilege('shovan', 'digital.attachment_audit_audit_id_seq', 'USAGE') THEN
        RAISE EXCEPTION 'Post-deployment validation: role shovan lacks required sequence privileges';
    END IF;

    IF NOT has_function_privilege(
            'shovan', 'digital.maintain_attachment_quota_reservation()', 'EXECUTE')
       OR NOT has_function_privilege(
            'shovan', 'digital.enforce_attachment_client_uploader_tenant()', 'EXECUTE') THEN
        RAISE EXCEPTION 'Post-deployment validation: role shovan lacks required function privileges';
    END IF;

    RAISE NOTICE
        'CBS Support messaging/attachment schema validation passed in database %',
        current_database();
END
$post_deployment_validation$;

COMMIT;
