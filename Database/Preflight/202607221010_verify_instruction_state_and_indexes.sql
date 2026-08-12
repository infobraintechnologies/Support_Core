-- Read-only readiness checks for state, tenant, conversation, and index migrations.
-- No INSERT, UPDATE, DELETE, DDL, or transaction control.

-- Completion values; review before adding NOT NULL.
SELECT completed, count(*) AS record_count
FROM digital.instructions
GROUP BY completed
ORDER BY completed NULLS FIRST;

-- Notification values; review before adding constraints.
SELECT notification_seen_by_admin, notification_seen_by_client, count(*) AS record_count
FROM digital.instructions
GROUP BY notification_seen_by_admin, notification_seen_by_client
ORDER BY notification_seen_by_admin NULLS FIRST, notification_seen_by_client NULLS FIRST;

-- Client-facing support types without a tenant. Expected: zero rows.
SELECT id, inst_category_id, inst_type_id, instruction_id
FROM digital.instructions
WHERE inst_type_id IN (100, 101, 110, 111, 112, 113, 114, 115, 116, 117, 121, 122)
  AND client_id IS NULL
ORDER BY id
LIMIT 200;

-- Replies whose root has a different tenant. Expected: zero rows.
SELECT reply.id AS reply_id,
       reply.client_id AS reply_client_id,
       reply.instruction_id AS root_id,
       root.client_id AS root_client_id
FROM digital.instructions AS reply
JOIN digital.instructions AS root
  ON root.id = reply.instruction_id
WHERE reply.instruction_id IS NOT NULL
  AND reply.id <> reply.instruction_id
  AND reply.client_id IS DISTINCT FROM root.client_id
ORDER BY reply.id
LIMIT 200;

-- Replies targeting another reply. Expected: zero rows.
SELECT reply.id AS reply_id,
       reply.instruction_id AS referenced_instruction_id,
       root.instruction_id AS referenced_instruction_root_id
FROM digital.instructions AS reply
JOIN digital.instructions AS root
  ON root.id = reply.instruction_id
WHERE reply.instruction_id IS NOT NULL
  AND reply.id <> reply.instruction_id
  AND root.instruction_id IS DISTINCT FROM root.id
ORDER BY reply.id
LIMIT 200;

-- Existing indexes; pair with EXPLAIN (ANALYZE, BUFFERS) before adding candidates.
SELECT indexname, indexdef
FROM pg_indexes
WHERE schemaname = 'digital'
  AND tablename = 'instructions'
ORDER BY indexname;
