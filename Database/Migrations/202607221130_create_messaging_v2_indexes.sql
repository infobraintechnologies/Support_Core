-- CBS Support database migration
-- Version: 202607221130_create_messaging_v2_indexes
-- Purpose: Build Messaging V2 uniqueness and query indexes without long blocking
--          table locks on production-sized instruction history.
-- Owned objects: indexes in the digital schema only.
-- Preconditions: 202607221120_backfill_messaging_v2_history has been applied.
-- migration-transaction: false
-- Transactional: No; PostgreSQL forbids CREATE INDEX CONCURRENTLY in a transaction.
-- Rollback/forward-fix: Each statement is rerunnable through IF NOT EXISTS. If the
-- runner stops partway, inspect invalid indexes, drop only the exact invalid index,
-- and rerun. Correct deployed definitions with a new concurrent forward-fix.

CREATE UNIQUE INDEX CONCURRENTLY IF NOT EXISTS ix_instructions_conversation_sequence_unique
ON digital.instructions (instruction_id, conversation_sequence)
WHERE instruction_id IS NOT NULL;

-- migration-command-boundary

CREATE UNIQUE INDEX CONCURRENTLY IF NOT EXISTS ix_instructions_client_message_unique
ON digital.instructions (client_message_id)
WHERE client_message_id IS NOT NULL;

-- migration-command-boundary

CREATE UNIQUE INDEX CONCURRENTLY IF NOT EXISTS ix_conversation_access_group_unique
ON digital.conversation_access (client_id)
WHERE conversation_kind = 'Group';

-- migration-command-boundary

CREATE UNIQUE INDEX CONCURRENTLY IF NOT EXISTS ix_conversation_access_active_private_pair_unique
ON digital.conversation_access (client_id, client_user_id, admin_user_id)
WHERE conversation_kind = 'Private' AND state = 'Active';

-- migration-command-boundary

CREATE UNIQUE INDEX CONCURRENTLY IF NOT EXISTS ix_conversation_read_cursors_admin_unique
ON digital.conversation_read_cursors (conversation_id, admin_user_id)
WHERE principal_kind = 'Admin';

-- migration-command-boundary

CREATE UNIQUE INDEX CONCURRENTLY IF NOT EXISTS ix_conversation_read_cursors_client_unique
ON digital.conversation_read_cursors (conversation_id, client_user_id)
WHERE principal_kind = 'Client';

-- migration-command-boundary

CREATE INDEX CONCURRENTLY IF NOT EXISTS ix_conversation_access_client_state_kind
ON digital.conversation_access (client_id, state, conversation_kind, conversation_id);

-- migration-command-boundary

CREATE INDEX CONCURRENTLY IF NOT EXISTS ix_conversation_outbox_dispatch
ON digital.conversation_outbox (available_at, event_id)
WHERE processed_at IS NULL AND dead_lettered_at IS NULL;

-- migration-command-boundary

CREATE INDEX CONCURRENTLY IF NOT EXISTS ix_conversation_audit_conversation_occurred
ON digital.conversation_audit (conversation_id, occurred_at DESC, audit_id DESC);

-- migration-command-boundary

CREATE INDEX CONCURRENTLY IF NOT EXISTS ix_conversation_audit_client_occurred
ON digital.conversation_audit (client_id, occurred_at DESC, audit_id DESC);
