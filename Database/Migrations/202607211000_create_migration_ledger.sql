-- migration-transaction: true

CREATE TABLE digital.schema_migrations (
    version varchar(64) NOT NULL,
    checksum char(64) NOT NULL,
    applied_at timestamptz NOT NULL DEFAULT now(),
    applied_by varchar(128) NOT NULL,
    execution_ms integer NOT NULL,
    CONSTRAINT pk_schema_migrations PRIMARY KEY (version),
    CONSTRAINT ck_schema_migrations_checksum_hex
        CHECK (checksum ~ '^[0-9a-f]{64}$'),
    CONSTRAINT ck_schema_migrations_execution_ms_nonnegative
        CHECK (execution_ms >= 0),
    CONSTRAINT ck_schema_migrations_applied_by_nonempty
        CHECK (btrim(applied_by) <> '')
);

COMMENT ON TABLE digital.schema_migrations IS
    'CBS Support migration ledger. Rows are inserted only by the approved migration runner after a script succeeds.';

COMMENT ON COLUMN digital.schema_migrations.version IS
    'Timestamped migration filename without the .sql extension.';

COMMENT ON COLUMN digital.schema_migrations.checksum IS
    'Lowercase SHA-256 checksum of the immutable migration file content.';
