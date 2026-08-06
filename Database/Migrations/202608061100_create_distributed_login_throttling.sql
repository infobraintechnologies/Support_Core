-- CBS Support database migration
-- Version: 202608061100_create_distributed_login_throttling
-- Purpose: shared atomic login throttling state for all API instances.
-- Preconditions: the digital schema exists and the application role can use it.
-- migration-transaction: true
-- Rollback/forward-fix: retain throttle rows while the feature is deployed;
-- correct future behavior with an ordered forward migration.

CREATE TABLE digital.login_throttle_buckets (
    bucket_kind varchar(16) NOT NULL,
    bucket_key varchar(64) NOT NULL,
    window_started_at timestamptz NOT NULL,
    request_count integer NOT NULL DEFAULT 0,
    failed_attempts integer NOT NULL DEFAULT 0,
    backoff_level smallint NOT NULL DEFAULT 0,
    blocked_until timestamptz,
    last_touched_at timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT pk_login_throttle_buckets PRIMARY KEY (bucket_kind, bucket_key),
    CONSTRAINT ck_login_throttle_bucket_kind
        CHECK (bucket_kind IN ('source', 'account')),
    CONSTRAINT ck_login_throttle_bucket_key
        CHECK (length(bucket_key) = 64 AND bucket_key ~ '^[0-9A-Fa-f]{64}$'),
    CONSTRAINT ck_login_throttle_counts_nonnegative
        CHECK (request_count >= 0 AND failed_attempts >= 0),
    CONSTRAINT ck_login_throttle_backoff_level
        CHECK (backoff_level BETWEEN 0 AND 31),
    CONSTRAINT ck_login_throttle_blocked_timestamp
        CHECK (blocked_until IS NULL OR blocked_until >= window_started_at)
);

CREATE INDEX ix_login_throttle_buckets_cleanup
ON digital.login_throttle_buckets (last_touched_at);

REVOKE ALL ON TABLE digital.login_throttle_buckets FROM PUBLIC;

-- The established application role receives only the DML needed by the
-- throttling repository. Environments using another role must grant the same
-- minimal privileges during controlled deployment.
DO $application_role$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'shovan') THEN
        GRANT SELECT, INSERT, UPDATE, DELETE
            ON TABLE digital.login_throttle_buckets TO shovan;
        REVOKE TRUNCATE ON TABLE digital.login_throttle_buckets FROM shovan;
    END IF;
END
$application_role$;

COMMENT ON TABLE digital.login_throttle_buckets IS
    'Bounded distributed login throttle state. bucket_key is a SHA-256 digest and must never be logged or exposed.';
COMMENT ON COLUMN digital.login_throttle_buckets.bucket_kind IS
    'source is the client-IP/network fixed-window counter; account is the normalized-account plus source backoff state.';
