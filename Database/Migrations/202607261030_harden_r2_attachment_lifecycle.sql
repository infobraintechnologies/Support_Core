-- CBS Support database migration
-- Version: 202607261030_harden_r2_attachment_lifecycle
-- Purpose: harden asynchronous validation, deletion retry diagnostics, and
--          deterministic object ownership after the initial R2 attachment schema.
-- migration-transaction: true
-- Rollback/forward-fix: durable attachment state must be corrected with a later
-- ordered forward migration after upload intents have been issued.

ALTER TABLE digital.attachments
    DROP CONSTRAINT ck_attachments_sizes;

ALTER TABLE digital.attachments
    ADD CONSTRAINT ck_attachments_sizes
        CHECK (
            declared_size BETWEEN 1 AND 10485760
            AND (actual_size IS NULL OR actual_size >= 0)
            AND reservation_bytes BETWEEN 0 AND 10485760),
    ADD COLUMN last_error_code varchar(64),
    ADD COLUMN deletion_attempt_count integer NOT NULL DEFAULT 0,
    ADD CONSTRAINT ck_attachments_last_error_code
        CHECK (last_error_code IS NULL OR btrim(last_error_code) <> ''),
    ADD CONSTRAINT ck_attachments_deletion_attempt_nonnegative
        CHECK (deletion_attempt_count >= 0);

CREATE UNIQUE INDEX uq_attachments_quarantine_key
ON digital.attachments (quarantine_key)
WHERE quarantine_key IS NOT NULL;

CREATE UNIQUE INDEX uq_attachments_ready_key
ON digital.attachments (ready_key)
WHERE ready_key IS NOT NULL;

CREATE INDEX ix_attachments_ready_quarantine_cleanup
ON digital.attachments (next_attempt_at, ready_at, id)
WHERE state = 'Ready' AND quarantine_key IS NOT NULL;
