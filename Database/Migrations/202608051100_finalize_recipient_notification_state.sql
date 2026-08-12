-- Finalize recipient-specific notification state and record unsafe legacy rows.
-- Preconditions: 202608051000_add_case_notification_delivery.sql is applied.
-- migration-transaction: true
-- Forward-fix only; retain review records and do not recreate legacy read state.

CREATE INDEX ix_case_notifications_client_recipient_unread_created
ON digital.case_notifications (client_id, client_user_id, created_at DESC, notification_id DESC)
WHERE recipient_kind = 'Client' AND read_at IS NULL;

CREATE INDEX ix_case_notifications_admin_recipient_unread_created
ON digital.case_notifications (admin_user_id, created_at DESC, notification_id DESC)
WHERE recipient_kind = 'Admin' AND read_at IS NULL;

CREATE INDEX ix_case_notifications_event_id
ON digital.case_notifications (event_id, notification_id);

CREATE TABLE digital.notification_backfill_review (
    instruction_id bigint NOT NULL,
    client_id bigint,
    legacy_created_at timestamptz,
    legacy_admin_seen smallint,
    legacy_client_seen smallint,
    reason varchar(160) NOT NULL,
    recorded_at timestamptz NOT NULL DEFAULT now(),
    resolution varchar(32) NOT NULL DEFAULT 'NeedsReview',
    resolved_at timestamptz,
    resolved_by_admin_user_id integer,
    notes varchar(1000),
    CONSTRAINT pk_notification_backfill_review PRIMARY KEY (instruction_id),
    CONSTRAINT ck_notification_backfill_review_reason_nonempty CHECK (btrim(reason) <> ''),
    CONSTRAINT ck_notification_backfill_review_resolution
        CHECK (resolution IN ('NeedsReview', 'Resolved', 'Dismissed')),
    CONSTRAINT ck_notification_backfill_review_resolution_state CHECK (
        (resolution = 'NeedsReview' AND resolved_at IS NULL AND resolved_by_admin_user_id IS NULL)
        OR (resolution IN ('Resolved', 'Dismissed') AND resolved_at IS NOT NULL AND resolved_by_admin_user_id IS NOT NULL)
    ),
    CONSTRAINT fk_notification_backfill_review_instruction
        FOREIGN KEY (instruction_id) REFERENCES digital.instructions(id),
    CONSTRAINT fk_notification_backfill_review_admin
        FOREIGN KEY (resolved_by_admin_user_id) REFERENCES admin.users(id)
);

-- Legacy flags do not identify a recipient; unresolved rows are recorded for review.
INSERT INTO digital.notification_backfill_review (
    instruction_id, client_id, legacy_created_at, legacy_admin_seen,
    legacy_client_seen, reason)
SELECT instruction.id,
       instruction.client_id,
       COALESCE(instruction.insert_date, instruction.datetime),
       NULLIF(to_jsonb(instruction) ->> 'notification_seen_by_admin', '')::smallint,
       NULLIF(to_jsonb(instruction) ->> 'notification_seen_by_client', '')::smallint,
       'legacy_flags_do_not_identify_recipient'
FROM digital.instructions instruction
WHERE COALESCE(NULLIF(to_jsonb(instruction) ->> 'notification_seen_by_admin', '')::smallint, 0) = 0
   OR COALESCE(NULLIF(to_jsonb(instruction) ->> 'notification_seen_by_client', '')::smallint, 0) = 0
ON CONFLICT (instruction_id) DO NOTHING;

REVOKE ALL ON TABLE digital.notification_backfill_review FROM PUBLIC;

DO $application_role$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'shovan') THEN
        GRANT SELECT, INSERT, UPDATE (resolution, resolved_at, resolved_by_admin_user_id, notes)
            ON TABLE digital.notification_backfill_review TO shovan;
        REVOKE DELETE, TRUNCATE ON TABLE digital.notification_backfill_review FROM shovan;
    END IF;
END
$application_role$;

COMMENT ON TABLE digital.notification_backfill_review IS
    'Explicit unresolved legacy notification flags. No recipient rows are created from ambiguous historic tenant/global flags.';
COMMENT ON COLUMN digital.case_notifications.read_at IS
    'Per-recipient UTC read timestamp. NULL means unread; this is the only authoritative notification read state.';
