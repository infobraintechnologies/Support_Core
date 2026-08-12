-- Use quarantine-first structural attachment validation; retain scanning states.
-- migration-transaction: true
-- Forward-fix only; never relabel StructurallyValidated as malware-scanned.

ALTER TABLE digital.attachments
    DROP CONSTRAINT ck_attachments_state,
    DROP CONSTRAINT ck_attachments_ready_shape,
    DROP CONSTRAINT ck_attachments_sizes;

DROP INDEX digital.ix_attachments_client_active_storage;
DROP INDEX digital.ix_attachments_scan_claim;

WITH reset_for_structural_validation AS (
    UPDATE digital.attachments
    SET state = 'Uploaded',
        lease_owner = NULL,
        lease_until = NULL,
        next_attempt_at = now(),
        updated_at = now(),
        last_error_code = NULL
    WHERE state = 'Scanning'
    RETURNING id, client_id
)
INSERT INTO digital.attachment_audit (
    attachment_id, client_id, action, actor_kind, occurred_at, details)
SELECT id,
       client_id,
       'SecurityModeMigration',
       'System',
       now(),
       jsonb_build_object(
           'fromState', 'Scanning',
           'toState', 'Uploaded',
           'securityMode', 'StructuralValidationOnly')
FROM reset_for_structural_validation;

ALTER TABLE digital.attachments
    ADD CONSTRAINT ck_attachments_state
        CHECK (state IN (
            'PendingUpload','Uploaded','StructuralValidation','StructurallyValidated',
            'Scanning','Promoting','Ready','Rejected','ScanFailed',
            'DeletePending','Deleted','Expired')),
    ADD CONSTRAINT ck_attachments_ready_shape
        CHECK (
            state NOT IN ('StructurallyValidated','Ready','Promoting')
            OR (
                ready_key IS NOT NULL
                AND source_etag IS NOT NULL
                AND sha256 IS NOT NULL
                AND actual_size IS NOT NULL
                AND detected_media_type IS NOT NULL
            )),
    ADD CONSTRAINT ck_attachments_sizes
        CHECK (
            declared_size BETWEEN 1 AND 10485760
            AND (actual_size IS NULL OR actual_size >= 0)
            AND reservation_bytes >= 0
            AND (
                state NOT IN (
                    'PendingUpload','Uploaded','StructuralValidation',
                    'StructurallyValidated','Scanning','Promoting','Ready','DeletePending')
                OR reservation_bytes >= GREATEST(
                    declared_size,
                    COALESCE(actual_size, 0))
            ));

CREATE INDEX ix_attachments_client_active_storage
ON digital.attachments (client_id, state)
WHERE state IN (
    'PendingUpload','Uploaded','StructuralValidation','StructurallyValidated',
    'Scanning','Promoting','Ready','DeletePending');

CREATE INDEX ix_attachments_scan_claim
ON digital.attachments (next_attempt_at, created_at)
WHERE state IN (
    'Uploaded','StructuralValidation','StructurallyValidated','Scanning','Promoting');

CREATE OR REPLACE FUNCTION digital.maintain_attachment_quota_reservation()
RETURNS trigger
LANGUAGE plpgsql
AS $function$
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
$function$;
