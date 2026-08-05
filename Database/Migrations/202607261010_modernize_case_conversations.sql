-- CBS Support database migration
-- Version: 202607261010_modernize_case_conversations
-- Purpose: move ticket/inquiry roots and replies onto sequence-aware conversation
--          access, idempotent sends, audit, and transactional outbox.
-- Preconditions: review 202607261000_verify_case_conversation_readiness.sql.
-- migration-transaction: true
-- Rollback/forward-fix: sequence/access values become durable when the application
-- starts writing. Correct problems with an ordered forward-fix; do not resequence.

DO $guard$
BEGIN
    IF EXISTS (
        SELECT 1
        FROM digital.instructions message
        LEFT JOIN digital.instructions root ON root.id = message.instruction_id
        WHERE message.inst_type_id IN (110,111,112,113,114,115,116,117,121,122)
          AND (
                message.instruction_id IS NULL
                OR root.id IS NULL
                OR root.instruction_id IS DISTINCT FROM root.id
          )
    ) THEN
        RAISE EXCEPTION 'Case migration found an orphan or noncanonical root reference';
    END IF;

    IF EXISTS (
        SELECT 1
        FROM digital.instructions message
        JOIN digital.instructions root ON root.id = message.instruction_id
        WHERE message.id <> root.id
          AND (
                message.inst_type_id IN (110,111,112,113,114,115,116,117,121,122)
                OR (
                    (root.inst_type_id BETWEEN 110 AND 117
                        AND root.inst_category_id = 101)
                    OR (root.inst_type_id IN (121,122)
                        AND root.inst_category_id = 102)
                )
          )
          AND (
                message.client_id IS DISTINCT FROM root.client_id
                OR message.inst_type_id IS DISTINCT FROM root.inst_type_id
                OR message.inst_category_id IS DISTINCT FROM root.inst_category_id
          )
    ) THEN
        RAISE EXCEPTION 'Case migration found tenant/type/category mismatch';
    END IF;

    IF EXISTS (
        SELECT 1
        FROM digital.instructions
        WHERE instruction_id = id
          AND (
                (inst_type_id BETWEEN 110 AND 117
                    AND (inst_category_id IS DISTINCT FROM 101 OR client_id IS NULL))
                OR (inst_type_id IN (121,122)
                    AND (inst_category_id IS DISTINCT FROM 102 OR client_id IS NULL))
          )
    ) THEN
        RAISE EXCEPTION 'Case migration found an invalid root mapping';
    END IF;

    IF EXISTS (
        SELECT 1
        FROM digital.conversation_access access
        JOIN digital.instructions root ON root.id = access.conversation_id
        WHERE root.instruction_id = root.id
          AND (
                (root.inst_type_id BETWEEN 110 AND 117
                    AND root.inst_category_id = 101)
                OR (root.inst_type_id IN (121,122)
                    AND root.inst_category_id = 102)
          )
          AND (
                access.client_id IS DISTINCT FROM root.client_id
                OR access.conversation_kind IS DISTINCT FROM
                    CASE WHEN root.inst_category_id = 101 THEN 'Ticket' ELSE 'Inquiry' END
                OR access.state IS DISTINCT FROM 'Active'
                OR access.client_user_id IS NOT NULL
                OR access.admin_user_id IS NOT NULL
          )
    ) THEN
        RAISE EXCEPTION 'Case migration found conflicting existing access metadata';
    END IF;
END
$guard$;

ALTER TABLE digital.conversation_access
    DROP CONSTRAINT ck_conversation_access_kind,
    DROP CONSTRAINT ck_conversation_access_participants;

ALTER TABLE digital.conversation_access
    ADD CONSTRAINT ck_conversation_access_kind
        CHECK (conversation_kind IN ('Group', 'Private', 'Ticket', 'Inquiry')),
    ADD CONSTRAINT ck_conversation_access_participants
        CHECK (
            (conversation_kind IN ('Group', 'Ticket', 'Inquiry')
                AND state = 'Active'
                AND client_user_id IS NULL
                AND admin_user_id IS NULL)
            OR
            (conversation_kind = 'Private'
                AND (
                    state = 'NeedsReview'
                    OR (state IN ('Active', 'Archived')
                        AND client_user_id IS NOT NULL
                        AND admin_user_id IS NOT NULL)
                ))
        );

ALTER TABLE digital.conversation_outbox
    DROP CONSTRAINT ck_conversation_outbox_kind,
    DROP CONSTRAINT ck_conversation_outbox_participants;

ALTER TABLE digital.conversation_outbox
    ADD CONSTRAINT ck_conversation_outbox_kind
        CHECK (conversation_kind IN ('Group', 'Private', 'Ticket', 'Inquiry')),
    ADD CONSTRAINT ck_conversation_outbox_participants
        CHECK (
            (conversation_kind IN ('Group', 'Ticket', 'Inquiry')
                AND client_user_id IS NULL
                AND admin_user_id IS NULL)
            OR
            (conversation_kind = 'Private'
                AND client_user_id IS NOT NULL
                AND admin_user_id IS NOT NULL)
        );

CREATE TEMPORARY TABLE case_sequence_assignment (
    instruction_record_id bigint PRIMARY KEY,
    assigned_sequence bigint NOT NULL
) ON COMMIT DROP;

INSERT INTO case_sequence_assignment (instruction_record_id, assigned_sequence)
SELECT root.id, 1
FROM digital.instructions root
WHERE root.instruction_id = root.id
  AND (
        (root.inst_type_id BETWEEN 110 AND 117 AND root.inst_category_id = 101)
        OR (root.inst_type_id IN (121,122) AND root.inst_category_id = 102)
  )
UNION ALL
SELECT message.id,
       row_number() OVER (
           PARTITION BY message.instruction_id
           ORDER BY COALESCE(message.datetime, message.insert_date), message.id
       ) + 1
FROM digital.instructions message
JOIN digital.instructions root ON root.id = message.instruction_id
WHERE message.id <> root.id
  AND (
        (root.inst_type_id BETWEEN 110 AND 117 AND root.inst_category_id = 101)
        OR (root.inst_type_id IN (121,122) AND root.inst_category_id = 102)
  );

-- Avoid transient collisions with the existing unique sequence index.
UPDATE digital.instructions instruction
SET conversation_sequence = -instruction.id
FROM case_sequence_assignment assignment
WHERE instruction.id = assignment.instruction_record_id;

UPDATE digital.instructions instruction
SET conversation_sequence = assignment.assigned_sequence
FROM case_sequence_assignment assignment
WHERE instruction.id = assignment.instruction_record_id;

INSERT INTO digital.conversation_access (
    conversation_id, client_id, conversation_kind, state,
    client_user_id, admin_user_id, version, created_at)
SELECT root.id,
       root.client_id,
       CASE WHEN root.inst_category_id = 101 THEN 'Ticket' ELSE 'Inquiry' END,
       'Active',
       NULL,
       NULL,
       1,
       COALESCE(root.datetime, root.insert_date)
FROM digital.instructions root
WHERE root.instruction_id = root.id
  AND (
        (root.inst_type_id BETWEEN 110 AND 117 AND root.inst_category_id = 101)
        OR (root.inst_type_id IN (121,122) AND root.inst_category_id = 102)
  )
ON CONFLICT (conversation_id) DO NOTHING;

INSERT INTO digital.conversation_sequences (conversation_id, next_sequence)
SELECT assignment_root.instruction_id,
       max(assignment.assigned_sequence) + 1
FROM case_sequence_assignment assignment
JOIN digital.instructions assignment_root
  ON assignment_root.id = assignment.instruction_record_id
GROUP BY assignment_root.instruction_id
ON CONFLICT (conversation_id) DO UPDATE
SET next_sequence = EXCLUDED.next_sequence;

INSERT INTO digital.conversation_audit (
    conversation_id, client_id, action, actor_kind, occurred_at, details)
SELECT access.conversation_id,
       access.client_id,
       'CaseHistorySequenced',
       'System',
       now(),
       jsonb_build_object('migration', '202607261010_modernize_case_conversations')
FROM digital.conversation_access access
WHERE access.conversation_kind IN ('Ticket', 'Inquiry');

ALTER TABLE digital.instructions
    DROP CONSTRAINT ck_instructions_conversation_sequence_shape;

ALTER TABLE digital.instructions
    ADD CONSTRAINT ck_instructions_conversation_sequence_shape
    CHECK (
        (inst_type_id = 105
            AND client_message_id IS NULL)
        OR
        (instruction_id IS NULL
            AND conversation_sequence IS NULL
            AND client_message_id IS NULL)
        OR
        (instruction_id IS NULL
            AND conversation_sequence = 0
            AND client_message_id IS NULL
            AND NULLIF(btrim(instruction), '') IS NULL)
        OR
        (instruction_id IS NULL
            AND conversation_sequence = 1
            AND client_message_id IS NULL
            AND inst_type_id IN (110,111,112,113,114,115,116,117,121,122)
            AND NULLIF(btrim(instruction), '') IS NOT NULL)
        OR
        (instruction_id = id
            AND client_message_id IS NULL
            AND (
                (conversation_sequence = 0 AND NULLIF(btrim(instruction), '') IS NULL)
                OR (conversation_sequence > 0 AND NULLIF(btrim(instruction), '') IS NOT NULL)
            ))
        OR
        (instruction_id <> id
            AND conversation_sequence > 0
            AND (
                NULLIF(btrim(instruction), '') IS NOT NULL
                OR client_message_id IS NOT NULL
            ))
    )
    NOT VALID;

ALTER TABLE digital.instructions
    VALIDATE CONSTRAINT ck_instructions_conversation_sequence_shape;
