# Database migration operations

`Migrations` contains immutable, ordered schema/data changes owned by CBS Support.
`Preflight` contains read-only checks that must pass before a dependent migration is approved.

## Required execution contract

`CBSSupport.DatabaseMigrator` is the approved migration runner. It must be used
for migrations; pgAdmin remains appropriate for the read-only scripts under
`Preflight`.

The runner:

1. Acquire a PostgreSQL advisory lock so only one migration process runs.
2. Read `Migrations/*.sql` in lexical order.
3. Calculate the lowercase SHA-256 checksum of each file.
4. Compare each version and checksum with `digital.schema_migrations`.
5. Run an unapplied transactional migration and insert its ledger row only after the script succeeds, within the same transaction.
6. Reject an already-applied version whose checksum has changed.
7. Run explicitly non-transactional migrations, such as `CREATE INDEX CONCURRENTLY`, separately and record them only after success.

The first migration bootstraps `digital.schema_migrations`; the runner applies the
file and inserts its matching ledger record in the same bootstrap transaction.

Migrations must not include `BEGIN` or `COMMIT`; the runner owns the transaction.
Use `-- migration-transaction: false` only for migrations such as `CREATE INDEX
CONCURRENTLY`, which PostgreSQL does not permit inside a transaction.

Never edit an applied migration. Write a new timestamped forward-fix instead.

## Current sequence

1. Run `Preflight/202607211010_verify_instruction_principals.sql` against a
   sanitized copy of the target database and archive the result with the change
   review. It must confirm the externally owned `internal.clients.id` is a
   `NOT NULL` `integer` single-column primary/unique key while
   `digital.instructions.client_id` remains `bigint`; it does not alter
   `internal.clients`.
2. Apply the approved scripts through `CBSSupport.DatabaseMigrator`. It first creates and records `digital.schema_migrations`, then applies the canonical-principal migration.
3. Run `Preflight/202607221010_verify_instruction_state_and_indexes.sql` before approving the state, tenant-constraint, concurrency, and index migrations.
4. Run `Preflight/202607221100_verify_messaging_v2_readiness.sql` against a sanitized production-like copy. Resolve missing/noncanonical roots, tenant mismatches, tenantless group/private roots, and duplicate Group roots before approval. Archive the private-review inventory with the review. Duplicate Group roots require explicit business remediation; the migration never chooses a winner because a tenant may have only one Group conversation ever.
5. Apply `202607221110_create_messaging_v2_schema`, `202607221120_backfill_messaging_v2_history`, and the non-transactional `202607221130_create_messaging_v2_indexes` through the approved runner. The runner must not be interrupted during the concurrent-index migration unless the operator follows its invalid-index recovery note.
6. Run `Preflight/202607261000_verify_case_conversation_readiness.sql`. Resolve every orphan, tenant mismatch, and invalid ticket/inquiry type/category result before continuing. Historical replies may use the reviewed legacy `100` type/category sentinel.
7. Apply `202607261005_normalize_legacy_case_reply_shape.sql` separately. It
   locks instruction writes, repairs only same-tenant canonical case replies whose
   type/category dimensions are either `100` or already match the root, and fails
   closed for every other mismatch. Archive the applied checksum and repaired-row
   notice, then rerun the case readiness preflight; all four integrity result sets
   must return zero rows.
8. Apply `202607261010_modernize_case_conversations.sql` with
   `Attachments:Enabled=false`. Existing canonical ticket and inquiry roots become
   sequence `1`; replies are ordered by
   `COALESCE(datetime, insert_date), id` beginning at `2`; the allocator is
   upserted to `MAX(conversation_sequence) + 1`. The disabled application path
   must keep text history, sends, replay, and outbox delivery operational without
   querying attachment tables because `202607261020` has not run yet.
9. Keep attachments disabled for a documented, uninterrupted 24-hour observation
   gate after the Phase 1 deployment. Verify case history, sends, idempotent
   replay, allocator monotonicity, outbox dispatch, and SignalR delivery. Record
   the gate start/end timestamps and evidence. This repository change does not
   claim that the operational wait has occurred.
10. Apply the ordered attachment schema and forward-fix migrations while the
   feature remains disabled, including
   `202608031000_structural_attachment_validation_mode.sql`. Configure private R2
   and explicitly select `Attachments:SecurityMode=StructuralValidationOnly`.
   Validate the structural worker and record team approval of residual malware
   risk and compensating controls before any separately approved activation.

StructuralValidationOnly performs no malware scanning and does not require ClamAV,
port 3310, scanner health, or signature definitions. It must not be represented as
proving files malware-free. `MalwareScanning` remains a future explicit mode only.

The shared-test manual deployment contains a compatibility repair for an earlier
committed shape where the six owned Messaging V2/attachment `client_user_id`
columns were `bigint`. A rerun validates integer range, support-user existence,
and tenant membership before dropping only the six expected FKs, narrowing the
columns to `integer`, recreating the FKs, and validating them in the deployment
transaction. `ManualDeployments/20260803_verify_messaging_attachments_test.sql`
remains authoritative for the final integer column contract.

Messaging V2 backfill deliberately does not copy, clear, or otherwise rewrite
`instruction`. General Group/Private roots retain the earlier sentinel convention.
Canonical ticket/inquiry roots are normalized by the Phase 1 case migration to
sequence `1`, with replies following deterministically. New ticket/inquiry roots
must be inserted atomically at sequence `1` with their allocator initialized to
`2`.

The confirmed mapping for historical `client_auth_user_id` values is a prerequisite, not a replacement, for the preflight: the preflight also detects tenant mismatches and records the exact current foreign-key name.

## Commands

Preview the scripts and checksums without connecting to PostgreSQL:

```powershell
dotnet run --project .\CBSSupport.DatabaseMigrator -- --dry-run
```

For an approved local/test database, set the connection string only in the current
PowerShell session, then run the migrator:

```powershell
$env:CBSSUPPORT_MIGRATIONS_CONNECTION = '<connection string from approved secret storage>'
dotnet run --project .\CBSSupport.DatabaseMigrator -- --applied-by $env:USERNAME
Remove-Item Env:CBSSUPPORT_MIGRATIONS_CONNECTION
```

Never put the connection string in a migration, source file, command history shared
with others, or Git.

Attachment activation, R2 CORS, worker topology, health degradation, and the
24-hour gate are detailed in
[`Operations/ATTACHMENTS.md`](Operations/ATTACHMENTS.md).
