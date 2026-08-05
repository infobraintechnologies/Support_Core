-- CBS Support database migration
-- Version: 202607221120_backfill_messaging_v2_history
-- Purpose: Deterministically sequence legacy history, initialize allocators/access,
--          and validate root-sentinel integrity without rewriting message content.
-- Owned objects: Messaging V2 values and constraints in the digital schema only.
-- Preconditions:
--   1. 202607221110_create_messaging_v2_schema has been applied.
--   2. Readiness preflight reports no missing/noncanonical roots, tenant mismatches,
--      or tenantless type-100/type-101 roots.
-- migration-transaction: true
-- Transactional: Yes.
-- Rollback/forward-fix: Sequence assignments and access/audit backfill are durable.
-- Correct unexpected mappings with a reviewed forward-fix; never resequence live history.

DO $migration_guard$
BEGIN
    IF EXISTS (
        SELECT 1
        FROM digital.instructions AS message
        LEFT JOIN digital.instructions AS root
          ON root.id = message.instruction_id
        WHERE message.instruction_id IS NOT NULL
          AND (root.id IS NULL OR root.instruction_id IS DISTINCT FROM root.id)
    ) THEN
        RAISE EXCEPTION 'Messaging V2 backfill requires every linked instruction to reference a canonical root';
    END IF;

    IF EXISTS (
        SELECT 1
        FROM digital.instructions AS message
        JOIN digital.instructions AS root
          ON root.id = message.instruction_id
        WHERE message.id <> root.id
          AND message.client_id IS DISTINCT FROM root.client_id
    ) THEN
        RAISE EXCEPTION 'Messaging V2 backfill found a reply whose tenant differs from its root';
    END IF;

    IF EXISTS (
        SELECT 1
        FROM digital.instructions
        WHERE instruction_id = id
          AND inst_type_id IN (100, 101)
          AND client_id IS NULL
    ) THEN
        RAISE EXCEPTION 'Messaging V2 access backfill requires a tenant on every group/private root';
    END IF;

    IF EXISTS (
        SELECT 1
        FROM digital.instructions
        WHERE instruction_id = id
          AND inst_type_id = 100
          AND client_id IS NOT NULL
        GROUP BY client_id
        HAVING count(*) > 1
    ) THEN
        RAISE EXCEPTION 'Messaging V2 permits only one Group conversation ever per tenant; resolve duplicate legacy group roots explicitly';
    END IF;
END
$migration_guard$;

-- Materialize one immutable assignment map, then update in bounded ID batches.
-- The migrator still owns one atomic transaction, so readers never observe a
-- partially sequenced history. An empty/null root is the server-owned sequence-0
-- sentinel. A nonempty legacy root is itself sequence 1 with its ID/content intact.
CREATE TEMPORARY TABLE messaging_v2_sequence_backfill (
    instruction_record_id bigint NOT NULL,
    assigned_sequence bigint NOT NULL,
    CONSTRAINT pk_messaging_v2_sequence_backfill PRIMARY KEY (instruction_record_id)
) ON COMMIT DROP;

INSERT INTO messaging_v2_sequence_backfill (instruction_record_id, assigned_sequence)
SELECT root.id,
       CASE
           WHEN NULLIF(btrim(root.instruction), '') IS NULL THEN 0::bigint
           ELSE 1::bigint
       END
FROM digital.instructions AS root
WHERE root.instruction_id = root.id
UNION ALL
SELECT message.id,
       (row_number() OVER (
           PARTITION BY message.instruction_id
           ORDER BY COALESCE(message.datetime, message.insert_date), message.id
       ) + CASE
               WHEN NULLIF(btrim(root.instruction), '') IS NULL THEN 0
               ELSE 1
           END)::bigint
FROM digital.instructions AS message
JOIN digital.instructions AS root
  ON root.id = message.instruction_id
WHERE message.instruction_id IS NOT NULL
  AND message.instruction_id <> message.id;

DO $sequence_backfill$
DECLARE
    batch_upper_id bigint;
    last_updated_id bigint;
BEGIN
    LOOP
        SELECT max(batch.instruction_record_id)
        INTO batch_upper_id
        FROM (
            SELECT instruction_record_id
            FROM messaging_v2_sequence_backfill
            WHERE last_updated_id IS NULL
               OR instruction_record_id > last_updated_id
            ORDER BY instruction_record_id
            LIMIT 10000
        ) AS batch;

        EXIT WHEN batch_upper_id IS NULL;

        UPDATE digital.instructions AS instruction
        SET conversation_sequence = backfill.assigned_sequence
        FROM messaging_v2_sequence_backfill AS backfill
        WHERE instruction.id = backfill.instruction_record_id
          AND (last_updated_id IS NULL OR backfill.instruction_record_id > last_updated_id)
          AND backfill.instruction_record_id <= batch_upper_id;

        last_updated_id := batch_upper_id;
    END LOOP;
END
$sequence_backfill$;

ALTER TABLE digital.instructions
    ADD CONSTRAINT ck_instructions_conversation_sequence_shape
    CHECK (
        -- Ticket/inquiry/internal writers migrate in later feature slices. Their
        -- existing rows may be backfilled with a sequence, but new legacy writes
        -- remain valid without one until those commands move to Messaging V2.
        (COALESCE(inst_type_id, -1) NOT IN (100, 101)
            AND client_message_id IS NULL)
        OR
        (instruction_id IS NULL
            AND conversation_sequence IS NULL
            AND client_message_id IS NULL)
        OR
        -- Root creation is a two-statement operation inside one transaction: the
        -- identity is known after INSERT, then instruction_id is self-linked.
        (instruction_id IS NULL
            AND conversation_sequence = 0
            AND client_message_id IS NULL
            AND NULLIF(btrim(instruction), '') IS NULL)
        OR
        (instruction_id = id
            AND client_message_id IS NULL
            AND (
                (conversation_sequence = 0 AND NULLIF(btrim(instruction), '') IS NULL)
                OR
                (conversation_sequence > 0 AND NULLIF(btrim(instruction), '') IS NOT NULL)
            ))
        OR
        (instruction_id <> id AND conversation_sequence > 0)
    )
    NOT VALID;

ALTER TABLE digital.instructions
    VALIDATE CONSTRAINT ck_instructions_conversation_sequence_shape;

INSERT INTO digital.conversation_sequences (conversation_id, next_sequence)
SELECT root.id,
       COALESCE(max(message.conversation_sequence) + 1, 1) AS next_sequence
FROM digital.instructions AS root
LEFT JOIN digital.instructions AS message
  ON message.instruction_id = root.id
WHERE root.instruction_id = root.id
GROUP BY root.id;

-- Group conversations remain Active for their lifetime and are unique per tenant.
INSERT INTO digital.conversation_access (
    conversation_id, client_id, conversation_kind, state, version, created_at)
SELECT root.id,
       root.client_id,
       'Group',
       'Active',
       1,
       COALESCE(root.datetime, root.insert_date)
FROM digital.instructions AS root
WHERE root.instruction_id = root.id
  AND root.inst_type_id = 100;

-- Authorship on a message does not prove intended private-conversation membership.
-- Quarantine every legacy private root until an administrator supplies both principals.
INSERT INTO digital.conversation_access (
    conversation_id, client_id, conversation_kind, state, version, created_at, archived_at)
SELECT root.id,
       root.client_id,
       'Private',
       'NeedsReview',
       1,
       COALESCE(root.datetime, root.insert_date),
       NULL
FROM digital.instructions AS root
WHERE root.instruction_id = root.id
  AND root.inst_type_id = 101;

INSERT INTO digital.conversation_audit (
    conversation_id, client_id, action, actor_kind, occurred_at, details)
SELECT access.conversation_id,
       access.client_id,
       'LegacyAccessBackfilled',
       'System',
       access.created_at,
       jsonb_build_object(
           'conversationKind', access.conversation_kind,
           'initialState', access.state,
           'migration', '202607221120_backfill_messaging_v2_history')
FROM digital.conversation_access AS access;
