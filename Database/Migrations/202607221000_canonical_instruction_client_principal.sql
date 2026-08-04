-- CBS Support database migration
-- Version: 202607221000_canonical_instruction_client_principal
-- Purpose: Make internal.support_users the canonical client author referenced by
--          digital.instructions.client_auth_user_id.
-- Owned objects: digital.instructions constraints and values only.
-- Preconditions:
--   1. 202607211000_create_migration_ledger has been applied by the approved runner.
--   2. Preflight reports zero missing support-user mappings and zero tenant mismatches.
--   3. digital.instructions.insert_user is already nullable (this migration does not
--      alter its nullability).
--   4. digital.instructions.client_auth_user_id contains no values, so its live
--      bigint type can be narrowed to the integer key used by internal.support_users.id.
--   5. The application release that writes client_auth_user_id with insert_user = NULL
--      for Client actions is deployed with this migration.
-- migration-transaction: true
-- Transactional: Yes.
-- Rollback/forward-fix: The client_auth_user_id type change and historical
-- insert_user cleanup are intentionally irreversible.
-- Create a new forward-fix migration; do not restore the obsolete FK or repopulate
-- ambiguous admin values.

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

-- client_auth_user_id is canonical for Client-authored instructions. Legacy values
-- in insert_user are ambiguous admin IDs or accidental numeric overlaps and must not
-- remain an alternate author identity.
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
