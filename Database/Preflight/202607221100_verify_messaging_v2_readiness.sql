-- CBS Support read-only database preflight
-- Purpose: Measure readiness for the Messaging V2 schema and deterministic history backfill.
-- This script deliberately performs no INSERT, UPDATE, DELETE, DDL, or transaction control.

-- 1. The migration owns these names. Expected before first deployment: all rows report NULL.
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

-- 2. Existing columns with Messaging V2 names would require a forward-fix review.
-- Expected before first deployment: zero rows.
SELECT column_name, data_type, is_nullable
FROM information_schema.columns
WHERE table_schema = 'digital'
  AND table_name = 'instructions'
  AND column_name IN ('client_message_id', 'conversation_sequence')
ORDER BY column_name;

-- 3. Every linked instruction must reference a canonical root. Expected: zero rows.
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

-- 4. A reply and its root must have the same tenant, including NULL semantics.
-- Expected: zero rows.
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

-- 5. Messaging roots require a tenant before access rows can be created.
-- Expected: zero rows.
SELECT id, inst_type_id, datetime, insert_date
FROM digital.instructions
WHERE instruction_id = id
  AND inst_type_id IN (100, 101)
  AND client_id IS NULL
ORDER BY id
LIMIT 200;

-- 6. A tenant may have only one Group conversation ever. Expected: zero rows.
-- Duplicate legacy roots require an explicit business-reviewed remediation; the
-- migration will not silently select a winner or archive a group.
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

-- 7. Inventory legacy private roots. All are deliberately marked NeedsReview because
-- message authorship cannot prove the intended two-party membership.
SELECT client_id,
       count(*) AS private_roots_requiring_review
FROM digital.instructions
WHERE instruction_id = id
  AND inst_type_id = 101
  AND client_id IS NOT NULL
GROUP BY client_id
ORDER BY client_id;

-- 8. Nonempty roots are valid legacy first messages. They remain the same rows and
-- become positive sequence 1; their existing IDs and instruction content are preserved.
SELECT count(*) AS nonempty_root_messages_preserved
FROM digital.instructions
WHERE instruction_id = id
  AND NULLIF(btrim(instruction), '') IS NOT NULL;

-- 9. Estimate the backfill and index footprint.
SELECT count(*) FILTER (WHERE instruction_id = id) AS canonical_roots,
       count(*) FILTER (WHERE instruction_id IS NOT NULL AND instruction_id <> id) AS replies,
       count(*) FILTER (WHERE instruction_id IS NULL) AS unlinked_rows
FROM digital.instructions;

-- 10. Capture current instruction indexes for deployment review.
SELECT indexname, indexdef
FROM pg_indexes
WHERE schemaname = 'digital'
  AND tablename = 'instructions'
ORDER BY indexname;
