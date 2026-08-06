-- Persisted per-account session invalidation material.
-- Requires the pgcrypto gen_random_bytes(integer) function to be installed by
-- the database owner. CBS Support does not create extensions in externally
-- managed schemas.

DO $migration_guard$
BEGIN
    IF to_regclass('admin.users') IS NULL
       OR to_regclass('internal.support_users') IS NULL THEN
        RAISE EXCEPTION
            'Security-stamp migration requires admin.users and internal.support_users';
    END IF;

    IF to_regprocedure('gen_random_bytes(integer)') IS NULL THEN
        RAISE EXCEPTION
            'Security-stamp migration requires pgcrypto gen_random_bytes(integer)';
    END IF;
END
$migration_guard$;

ALTER TABLE admin.users
    ADD COLUMN IF NOT EXISTS security_stamp bytea;

ALTER TABLE internal.support_users
    ADD COLUMN IF NOT EXISTS security_stamp bytea;

DO $backfill_admin$
DECLARE
    changed_rows integer;
BEGIN
    LOOP
        UPDATE admin.users AS account
        SET security_stamp = gen_random_bytes(32)
        WHERE account.ctid IN (
            SELECT pending.ctid
            FROM admin.users AS pending
            WHERE pending.security_stamp IS NULL
            LIMIT 10000
        );

        GET DIAGNOSTICS changed_rows = ROW_COUNT;
        EXIT WHEN changed_rows = 0;
    END LOOP;
END
$backfill_admin$;

DO $backfill_client$
DECLARE
    changed_rows integer;
BEGIN
    LOOP
        UPDATE internal.support_users AS account
        SET security_stamp = gen_random_bytes(32)
        WHERE account.ctid IN (
            SELECT pending.ctid
            FROM internal.support_users AS pending
            WHERE pending.security_stamp IS NULL
            LIMIT 10000
        );

        GET DIAGNOSTICS changed_rows = ROW_COUNT;
        EXIT WHEN changed_rows = 0;
    END LOOP;
END
$backfill_client$;

ALTER TABLE admin.users
    ALTER COLUMN security_stamp SET NOT NULL;

ALTER TABLE internal.support_users
    ALTER COLUMN security_stamp SET NOT NULL;

DO $constraint_guard$
BEGIN
    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conrelid = 'admin.users'::regclass
          AND conname = 'ck_users_security_stamp_length'
    ) THEN
        ALTER TABLE admin.users
            ADD CONSTRAINT ck_users_security_stamp_length
            CHECK (octet_length(security_stamp) = 32);
    END IF;

    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conrelid = 'internal.support_users'::regclass
          AND conname = 'ck_support_users_security_stamp_length'
    ) THEN
        ALTER TABLE internal.support_users
            ADD CONSTRAINT ck_support_users_security_stamp_length
            CHECK (octet_length(security_stamp) = 32);
    END IF;
END
$constraint_guard$;

COMMENT ON COLUMN admin.users.security_stamp IS
    '32 cryptographically random bytes; changes invalidate all authentication state for the account';

COMMENT ON COLUMN internal.support_users.security_stamp IS
    '32 cryptographically random bytes; changes invalidate all authentication state for the account';
