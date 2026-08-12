-- Canonical Client author migration.
-- Preconditions: ledger applied; principal preflight is clean; insert_user is nullable;
-- client_auth_user_id is empty bigint; the Client writer release is deployed.
-- migration-transaction: true
-- Forward-fix only: the type change and historical cleanup are irreversible.

DO $precondition$
DECLARE
    blocking_foreign_keys text;
BEGIN
    IF format_type(
           (SELECT atttypid FROM pg_attribute
            WHERE attrelid = 'internal.support_users'::regclass
              AND attname = 'id' AND attnum > 0 AND NOT attisdropped),
           (SELECT atttypmod FROM pg_attribute
            WHERE attrelid = 'internal.support_users'::regclass
              AND attname = 'id' AND attnum > 0 AND NOT attisdropped)
       ) <> 'integer'
       OR format_type(
           (SELECT atttypid FROM pg_attribute
            WHERE attrelid = 'internal.support_users'::regclass
              AND attname = 'client_id' AND attnum > 0 AND NOT attisdropped),
           (SELECT atttypmod FROM pg_attribute
            WHERE attrelid = 'internal.support_users'::regclass
              AND attname = 'client_id' AND attnum > 0 AND NOT attisdropped)
       ) <> 'integer'
       OR EXISTS (
           SELECT 1
           FROM pg_attribute
           WHERE attrelid = 'internal.support_users'::regclass
             AND attname = 'user_id'
             AND attnum > 0
             AND NOT attisdropped
       ) THEN
        RAISE EXCEPTION
            'internal.support_users must match the inspected schema: integer id/client_id and no user_id column';
    END IF;

    IF EXISTS (
        SELECT 1
        FROM pg_attribute
        WHERE attrelid = 'digital.instructions'::regclass
          AND attname = 'insert_user'
          AND attnum > 0
          AND NOT attisdropped
          AND attnotnull
    ) THEN
        RAISE EXCEPTION
            'digital.instructions.insert_user must already be nullable; its owner must verify the live table before deployment';
    END IF;

    IF EXISTS (
        SELECT 1
        FROM digital.instructions
        WHERE client_auth_user_id IS NOT NULL
    ) THEN
        RAISE EXCEPTION
            'Refusing to narrow digital.instructions.client_auth_user_id while values exist';
    END IF;

    SELECT string_agg(constraint_row.conname, ', ' ORDER BY constraint_row.conname)
    INTO blocking_foreign_keys
    FROM pg_constraint constraint_row
    WHERE constraint_row.conrelid = 'digital.instructions'::regclass
      AND constraint_row.contype = 'f'
      AND constraint_row.conkey = ARRAY[(
          SELECT attnum
          FROM pg_attribute
          WHERE attrelid = 'digital.instructions'::regclass
            AND attname = 'client_auth_user_id'
            AND attnum > 0
            AND NOT attisdropped
      )]::smallint[];

    IF blocking_foreign_keys IS NOT NULL THEN
        RAISE EXCEPTION
            'The digital.instructions owner/DBA must drop client_auth_user_id foreign key(s) before deployment: %',
            blocking_foreign_keys;
    END IF;
END
$precondition$;

ALTER TABLE digital.instructions
    ALTER COLUMN client_auth_user_id TYPE integer
    USING client_auth_user_id::integer;

-- client_auth_user_id is canonical; legacy insert_user values are not retained.
UPDATE digital.instructions
SET insert_user = NULL
WHERE client_auth_user_id IS NOT NULL
  AND insert_user IS NOT NULL;

ALTER TABLE digital.instructions
    ADD CONSTRAINT fk_instructions_client_auth_support_user
    FOREIGN KEY (client_auth_user_id)
    REFERENCES internal.support_users(id)
    NOT VALID;

ALTER TABLE digital.instructions
    VALIDATE CONSTRAINT fk_instructions_client_auth_support_user;

ALTER TABLE digital.instructions
    ADD CONSTRAINT ck_instructions_client_author_exclusive
    CHECK (client_auth_user_id IS NULL OR insert_user IS NULL)
    NOT VALID;

ALTER TABLE digital.instructions
    VALIDATE CONSTRAINT ck_instructions_client_author_exclusive;

COMMENT ON COLUMN digital.instructions.client_auth_user_id IS
    'Canonical Client author. References internal.support_users(id); NULL for Admin-authored rows.';

COMMENT ON COLUMN digital.instructions.insert_user IS
    'Canonical Admin author/audit user. References admin.users(id); NULL for Client-authored rows.';
