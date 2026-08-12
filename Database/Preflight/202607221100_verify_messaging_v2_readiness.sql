-- Read-only Messaging V2 schema/backfill readiness checks.
-- No INSERT, UPDATE, DELETE, DDL, or transaction control.

-- Owned objects. Expected before first deployment: no existing relations.
SELECT owned_object.object_name,
       to_regclass(owned_object.object_name) AS existing_relation
FROM (VALUES
    ('digital.conversation_access'),
    ('digital.conversation_sequences'),
    ('digital.conversation_read_cursors'),
    ('digital.conversation_outbox'),
    ('digital.conversation_audit')
) AS owned_object(object_name)
ORDER BY owned_object.object_name;

-- Existing Messaging V2 columns. Expected: zero rows.
SELECT column_name, data_type, is_nullable
FROM information_schema.columns
WHERE table_schema = 'digital'
  AND table_name = 'instructions'
  AND column_name IN ('client_message_id', 'conversation_sequence')
ORDER BY column_name;

-- Linked instructions must reference canonical roots. Expected: zero rows.
SELECT message.id AS message_id,
       message.instruction_id AS referenced_id,
       root.instruction_id AS referenced_root_id
FROM digital.instructions AS message
LEFT JOIN digital.instructions AS root
  ON root.id = message.instruction_id
WHERE message.instruction_id IS NOT NULL
  AND (root.id IS NULL OR root.instruction_id IS DISTINCT FROM root.id)
ORDER BY message.id
LIMIT 200;

-- Reply/root tenant mismatches. Expected: zero rows.
SELECT message.id AS message_id,
       message.client_id AS message_client_id,
       root.id AS root_id,
       root.client_id AS root_client_id
FROM digital.instructions AS message
JOIN digital.instructions AS root
  ON root.id = message.instruction_id
WHERE message.id <> root.id
  AND message.client_id IS DISTINCT FROM root.client_id
ORDER BY message.id
LIMIT 200;

-- Tenantless Messaging roots. Expected: zero rows.
SELECT id, inst_type_id, datetime, insert_date
FROM digital.instructions
WHERE instruction_id = id
  AND inst_type_id IN (100, 101)
  AND client_id IS NULL
ORDER BY id
LIMIT 200;

-- Duplicate legacy Group roots. Expected: zero rows; remediate explicitly.
WITH group_roots AS (
    SELECT client_id,
           id AS root_id,
           count(*) OVER (PARTITION BY client_id) AS group_root_count
    FROM digital.instructions
    WHERE instruction_id = id
      AND inst_type_id = 100
      AND client_id IS NOT NULL
)
SELECT client_id, root_id, group_root_count
FROM group_roots
WHERE group_root_count > 1
ORDER BY client_id, root_id
LIMIT 200;

-- Inventory legacy Private roots; authorship cannot prove membership.
SELECT client_id,
       count(*) AS private_roots_requiring_review
FROM digital.instructions
WHERE instruction_id = id
  AND inst_type_id = 101
  AND client_id IS NOT NULL
GROUP BY client_id
ORDER BY client_id;

-- Nonempty roots become sequence 1 without changing IDs or content.
SELECT count(*) AS nonempty_root_messages_preserved
FROM digital.instructions
WHERE instruction_id = id
  AND NULLIF(btrim(instruction), '') IS NOT NULL;

-- Estimate backfill and index footprint.
SELECT count(*) FILTER (WHERE instruction_id = id) AS canonical_roots,
       count(*) FILTER (WHERE instruction_id IS NOT NULL AND instruction_id <> id) AS replies,
       count(*) FILTER (WHERE instruction_id IS NULL) AS unlinked_rows
FROM digital.instructions;

-- Capture instruction indexes for deployment review.
SELECT indexname, indexdef
FROM pg_indexes
WHERE schemaname = 'digital'
  AND tablename = 'instructions'
ORDER BY indexname;
