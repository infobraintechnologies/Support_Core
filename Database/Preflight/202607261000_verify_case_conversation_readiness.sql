-- Read-only case conversation readiness checks; no writes.

-- Replies without a canonical root. Expected: zero rows.
SELECT message.id AS message_id,
       message.instruction_id,
       root.instruction_id AS root_instruction_id
FROM digital.instructions message
LEFT JOIN digital.instructions root ON root.id = message.instruction_id
WHERE message.inst_type_id IN (110,111,112,113,114,115,116,117,121,122)
  AND (
        message.instruction_id IS NULL
        OR root.id IS NULL
        OR root.instruction_id IS DISTINCT FROM root.id
  )
ORDER BY message.id
LIMIT 200;

-- Root/reply tenant, type, or category mismatches. Expected: zero rows.
SELECT message.id AS message_id,
       message.client_id AS message_client_id,
       root.id AS root_id,
       root.client_id AS root_client_id,
       message.inst_type_id AS message_type_id,
       root.inst_type_id AS root_type_id,
       message.inst_category_id AS message_category_id,
       root.inst_category_id AS root_category_id
FROM digital.instructions message
JOIN digital.instructions root ON root.id = message.instruction_id
WHERE message.id <> root.id
  AND (
        message.inst_type_id IN (110,111,112,113,114,115,116,117,121,122)
        OR (
            (root.inst_type_id BETWEEN 110 AND 117 AND root.inst_category_id = 101)
            OR (root.inst_type_id IN (121,122) AND root.inst_category_id = 102)
        )
  )
  AND (
        message.client_id IS DISTINCT FROM root.client_id
        OR message.inst_type_id IS DISTINCT FROM root.inst_type_id
        OR message.inst_category_id IS DISTINCT FROM root.inst_category_id
  )
ORDER BY message.id
LIMIT 200;

-- Invalid canonical case roots. Expected: zero rows.
SELECT id, client_id, inst_type_id, inst_category_id
FROM digital.instructions
WHERE instruction_id = id
  AND (
        (inst_type_id BETWEEN 110 AND 117 AND inst_category_id IS DISTINCT FROM 101)
        OR (inst_type_id IN (121,122) AND inst_category_id IS DISTINCT FROM 102)
        OR (inst_type_id IN (110,111,112,113,114,115,116,117,121,122)
            AND client_id IS NULL)
  )
ORDER BY id
LIMIT 200;

-- Access rows must match the authoritative case shape. Expected: zero rows.
SELECT access.conversation_id,
       access.client_id AS access_client_id,
       root.client_id AS root_client_id,
       access.conversation_kind,
       access.state,
       access.client_user_id,
       access.admin_user_id
FROM digital.conversation_access access
JOIN digital.instructions root ON root.id = access.conversation_id
WHERE root.instruction_id = root.id
  AND (
        (root.inst_type_id BETWEEN 110 AND 117 AND root.inst_category_id = 101)
        OR (root.inst_type_id IN (121,122) AND root.inst_category_id = 102)
  )
  AND (
        access.client_id IS DISTINCT FROM root.client_id
        OR access.conversation_kind IS DISTINCT FROM
            CASE WHEN root.inst_category_id = 101 THEN 'Ticket' ELSE 'Inquiry' END
        OR access.state IS DISTINCT FROM 'Active'
        OR access.client_user_id IS NOT NULL
        OR access.admin_user_id IS NOT NULL
  )
ORDER BY access.conversation_id
LIMIT 200;

-- Inventory for the 24-hour post-Phase-1 observation gate.
SELECT count(*) FILTER (WHERE inst_type_id BETWEEN 110 AND 117) AS ticket_roots,
       count(*) FILTER (WHERE inst_type_id IN (121,122)) AS inquiry_roots,
       max(COALESCE(datetime, insert_date)) AS latest_case_timestamp
FROM digital.instructions
WHERE instruction_id = id;
