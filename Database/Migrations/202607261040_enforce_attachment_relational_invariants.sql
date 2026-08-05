-- CBS Support database migration
-- Version: 202607261040_enforce_attachment_relational_invariants
-- Purpose: make attachment tenant/uploader/message binding relationally authoritative
--          and keep physical DeletePending bytes in quota accounting.
-- Preconditions: review 202607261035_verify_attachment_relational_integrity.sql.
-- migration-transaction: true
-- Rollback/forward-fix: constraints protect live private objects and must be corrected
-- with a later ordered forward migration after upload intents are issued.

ALTER TABLE digital.conversation_access
    ADD CONSTRAINT uq_conversation_access_id_client
        UNIQUE (conversation_id, client_id);

ALTER TABLE digital.instructions
    ADD CONSTRAINT uq_instructions_id_conversation_client
        UNIQUE (id, instruction_id, client_id);

ALTER TABLE digital.attachments
    ADD CONSTRAINT uq_attachments_id_client
        UNIQUE (id, client_id),
    ADD CONSTRAINT fk_attachments_conversation_client
        FOREIGN KEY (conversation_id, client_id)
        REFERENCES digital.conversation_access(conversation_id, client_id),
    ADD CONSTRAINT fk_attachments_message_conversation_client
        FOREIGN KEY (message_id, conversation_id, client_id)
        REFERENCES digital.instructions(id, instruction_id, client_id);

ALTER TABLE digital.attachment_audit
    ADD CONSTRAINT fk_attachment_audit_attachment_client
        FOREIGN KEY (attachment_id, client_id)
        REFERENCES digital.attachments(id, client_id);

ALTER TABLE digital.attachments
    DROP CONSTRAINT ck_attachments_sizes;

UPDATE digital.attachments
SET reservation_bytes = GREATEST(
        reservation_bytes,
        declared_size,
        COALESCE(actual_size, 0))
WHERE state IN ('PendingUpload','Scanning','Promoting','Ready','DeletePending');

ALTER TABLE digital.attachments
    ADD CONSTRAINT ck_attachments_sizes
        CHECK (
            declared_size BETWEEN 1 AND 10485760
            AND (actual_size IS NULL OR actual_size >= 0)
            AND reservation_bytes >= 0
            AND (
                state NOT IN (
                    'PendingUpload','Scanning','Promoting','Ready','DeletePending')
                OR reservation_bytes >= GREATEST(
                    declared_size,
                    COALESCE(actual_size, 0))
            )),
    ADD CONSTRAINT ck_attachments_bound_retention
        CHECK (
            message_id IS NULL
            OR (
                expires_at IS NOT NULL
                AND expires_at >= bound_at + INTERVAL '365 days'
            )),
    ADD CONSTRAINT ck_attachments_ready_unbound_retention
        CHECK (
            state <> 'Ready'
            OR message_id IS NOT NULL
            OR (
                ready_at IS NOT NULL
                AND expires_at IS NOT NULL
                AND expires_at >= ready_at + INTERVAL '24 hours'
            ));

CREATE OR REPLACE FUNCTION digital.maintain_attachment_quota_reservation()
RETURNS trigger
LANGUAGE plpgsql
AS $function$
BEGIN
    IF NEW.state IN ('PendingUpload','Scanning','Promoting','Ready','DeletePending') THEN
        NEW.reservation_bytes := GREATEST(
            NEW.reservation_bytes,
            NEW.declared_size,
            COALESCE(NEW.actual_size, 0));
    END IF;
    RETURN NEW;
END
$function$;

CREATE TRIGGER trg_attachments_quota_reservation
BEFORE INSERT OR UPDATE OF state, declared_size, actual_size, reservation_bytes
ON digital.attachments
FOR EACH ROW
EXECUTE FUNCTION digital.maintain_attachment_quota_reservation();

CREATE OR REPLACE FUNCTION digital.enforce_attachment_client_uploader_tenant()
RETURNS trigger
LANGUAGE plpgsql
AS $function$
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
$function$;

CREATE TRIGGER trg_attachments_client_uploader_tenant
BEFORE INSERT OR UPDATE OF client_user_id, client_id
ON digital.attachments
FOR EACH ROW
EXECUTE FUNCTION digital.enforce_attachment_client_uploader_tenant();
