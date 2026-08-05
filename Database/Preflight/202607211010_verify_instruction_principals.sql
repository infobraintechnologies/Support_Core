-- Read-only principal/type readiness checks. This script performs no DDL/DML.

-- 1. Record the complete live column shape supplied by the owners of the two
-- external identity/tenant tables. internal.support_users.user_id is not an
-- expected authentication key; id is canonical unless this result proves otherwise.
SELECT
    columns.table_schema,
    columns.table_name,
    columns.ordinal_position,
    columns.column_name,
    columns.data_type,
    columns.udt_name,
    columns.is_nullable,
    columns.column_default
FROM information_schema.columns AS columns
WHERE columns.table_schema = 'internal'
  AND columns.table_name IN ('support_users', 'clients')
ORDER BY columns.table_name, columns.ordinal_position;

-- 2. Verify the confirmed external tenant key shape. This is a catalog-only
-- assertion: CBS Support must not alter the externally owned internal.clients table.
DO $clients_contract$
BEGIN
    IF NOT EXISTS (
        SELECT 1
        FROM pg_attribute AS column_row
        WHERE column_row.attrelid = 'internal.clients'::regclass
          AND column_row.attname = 'id'
          AND column_row.attnum > 0
          AND NOT column_row.attisdropped
          AND column_row.attnotnull
          AND format_type(column_row.atttypid, column_row.atttypmod) = 'integer'
    ) THEN
        RAISE EXCEPTION
            'internal.clients.id must match the confirmed live schema: integer and NOT NULL';
    END IF;

    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint AS key_constraint
        WHERE key_constraint.conrelid = 'internal.clients'::regclass
          AND key_constraint.contype IN ('p', 'u')
          AND key_constraint.conkey = ARRAY[(
              SELECT column_row.attnum
              FROM pg_attribute AS column_row
              WHERE column_row.attrelid = 'internal.clients'::regclass
                AND column_row.attname = 'id'
                AND column_row.attnum > 0
                AND NOT column_row.attisdropped
          )]::smallint[]
    ) THEN
        RAISE EXCEPTION
            'internal.clients.id must be a single-column primary or unique key';
    END IF;

    IF NOT EXISTS (
        SELECT 1
        FROM pg_attribute AS column_row
        WHERE column_row.attrelid = 'digital.instructions'::regclass
          AND column_row.attname = 'client_id'
          AND column_row.attnum > 0
          AND NOT column_row.attisdropped
          AND format_type(column_row.atttypid, column_row.atttypmod) = 'bigint'
    ) THEN
        RAISE EXCEPTION
            'digital.instructions.client_id must remain bigint';
    END IF;
END
$clients_contract$;

-- 3. Record the keys and foreign keys on the external tables for the review archive.
SELECT
    constrained_table.relname AS table_name,
    constraint_row.conname AS constraint_name,
    constraint_row.contype AS constraint_type,
    pg_get_constraintdef(constraint_row.oid) AS constraint_definition
FROM pg_constraint AS constraint_row
JOIN pg_class AS constrained_table
  ON constrained_table.oid = constraint_row.conrelid
WHERE constraint_row.conrelid IN (
        'internal.support_users'::regclass,
        'internal.clients'::regclass)
ORDER BY constrained_table.relname, constraint_row.conname;

-- 4. Record the actual existing client_auth_user_id foreign key and target table.
SELECT
    constraint_info.conname AS constraint_name,
    pg_get_constraintdef(constraint_info.oid) AS constraint_definition
FROM pg_constraint AS constraint_info
JOIN pg_class AS table_info
    ON table_info.oid = constraint_info.conrelid
JOIN pg_namespace AS schema_info
    ON schema_info.oid = table_info.relnamespace
JOIN pg_attribute AS column_info
    ON column_info.attrelid = table_info.oid
   AND column_info.attnum = ANY (constraint_info.conkey)
WHERE schema_info.nspname = 'digital'
  AND table_info.relname = 'instructions'
  AND constraint_info.contype = 'f'
  AND column_info.attname = 'client_auth_user_id';

-- 5. Confirm client_auth_user_id has the inspected bigint source type, is nullable,
-- and contains zero values before the owner/DBA narrows it to integer.
SELECT
    format_type(column_row.atttypid, column_row.atttypmod) AS formatted_type,
    NOT column_row.attnotnull AS is_nullable,
    (SELECT count(*)
     FROM digital.instructions
     WHERE client_auth_user_id IS NOT NULL) AS populated_value_count
FROM pg_attribute AS column_row
WHERE column_row.attrelid = 'digital.instructions'::regclass
  AND column_row.attname = 'client_auth_user_id'
  AND column_row.attnum > 0
  AND NOT column_row.attisdropped;

-- 6. Verify insert_user is already nullable. No migration may alter its nullability.
SELECT
    NOT column_row.attnotnull AS is_nullable
FROM pg_attribute AS column_row
WHERE column_row.attrelid = 'digital.instructions'::regclass
  AND column_row.attname = 'insert_user'
  AND column_row.attnum > 0
  AND NOT column_row.attisdropped;

-- 7. Every client_auth_user_id must resolve to a support-login identity.
SELECT
    instruction.id,
    instruction.client_id AS instruction_client_id,
    instruction.client_auth_user_id,
    instruction.insert_user,
    instruction.instruction_id,
    instruction.datetime
FROM digital.instructions AS instruction
LEFT JOIN internal.support_users AS support_user
    ON support_user.id = instruction.client_auth_user_id
WHERE instruction.client_auth_user_id IS NOT NULL
  AND support_user.id IS NULL
ORDER BY instruction.id
LIMIT 200;

-- 8. A client-authored instruction must belong to the same tenant as its support user.
SELECT
    instruction.id,
    instruction.client_id AS instruction_client_id,
    instruction.client_auth_user_id,
    support_user.client_id AS support_user_client_id,
    instruction.instruction_id,
    instruction.datetime
FROM digital.instructions AS instruction
JOIN internal.support_users AS support_user
    ON support_user.id = instruction.client_auth_user_id
WHERE instruction.client_auth_user_id IS NOT NULL
  AND support_user.client_id IS DISTINCT FROM instruction.client_id
ORDER BY instruction.id
LIMIT 200;

-- 9. Client-authored rows must not also use the admin-only insert_user column.
-- insert_user is already nullable in shared test and must remain so.
SELECT
    instruction.id,
    instruction.client_id,
    instruction.client_auth_user_id,
    instruction.insert_user,
    instruction.inst_category_id,
    instruction.inst_type_id,
    instruction.instruction_id
FROM digital.instructions AS instruction
WHERE instruction.client_auth_user_id IS NOT NULL
  AND instruction.insert_user IS NOT NULL
ORDER BY instruction.id
LIMIT 200;
