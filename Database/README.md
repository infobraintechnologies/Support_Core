# Database migration operations

`Migrations` contains immutable, ordered schema/data changes owned by CBS Support.
`Preflight` contains read-only checks that must pass before a dependent migration is approved.

## Required execution contract

Migrations are manually deployed through a reviewed operational runbook. Apply
the ordered SQL scripts with pgAdmin or psql by an authorized operator, then run
the corresponding read-only checks under `Preflight` and archive the evidence.
Do not add an automatic migration runner, EF Core migrations, or another
migration framework. Application startup must never mutate the database schema.

The existing `digital.schema_migrations` table and historical migration records
are retained as database history. New manual deployments must not create or
update that ledger unless a reviewed operational procedure explicitly requires it.
Follow each script's documented transaction and non-transactional-operation
requirements, including special handling for `CREATE INDEX CONCURRENTLY`.

Never edit an applied migration. Write a new timestamped forward-fix instead.

## Manual deployment procedure

1. Develop and review the timestamped SQL script and its read-only preflight.
2. Test the script against a disposable local PostgreSQL database created with
   `Bootstrap/local_migration_foundation.sql`.
3. When existing-data compatibility matters, test against an approved clone or
   backup and archive the preflight output.
4. Obtain application/DBA review before deployment.
5. An authorized operator applies the script manually through pgAdmin or `psql`.
6. Run the corresponding preflight and post-deployment verification SQL, then
   record the deployment evidence in the operational change record.

The API never applies schema changes at startup. Do not connect these checks or
tests to shared, staging, or production databases from a developer workstation.

## Current sequence

1. Run `Preflight/202607211010_verify_instruction_principals.sql` against a
   sanitized copy of the target database and archive the result with the change
   review. It must confirm the externally owned `internal.clients.id` is a
   `NOT NULL` `integer` single-column primary/unique key while
   `digital.instructions.client_id` remains `bigint`; it does not alter
   `internal.clients`.
2. Apply the approved scripts as a manually reviewed deployment step, then archive operator evidence. Do not add a new ledger row.
3. Run `Preflight/202607221010_verify_instruction_state_and_indexes.sql` before approving the state, tenant-constraint, concurrency, and index migrations.
4. Run `Preflight/202607221100_verify_messaging_v2_readiness.sql` against a sanitized production-like copy. Resolve missing/noncanonical roots, tenant mismatches, tenantless group/private roots, and duplicate Group roots before approval. Archive the private-review inventory with the review. Duplicate Group roots require explicit business remediation; the migration never chooses a winner because a tenant may have only one Group conversation ever.
5. Apply `202607221110_create_messaging_v2_schema`, `202607221120_backfill_messaging_v2_history`, and the non-transactional `202607221130_create_messaging_v2_indexes` as ordered, manually reviewed deployment steps. Follow the invalid-index recovery note if the concurrent-index operation is interrupted.
6. Apply `202608051200_complete_legacy_private_mapping_gate` as a reviewed
   deployment step. Its transactional guards prove canonical roots, access/root tenant
   ownership, participant tenant membership, required FKs, and active-pair
   uniqueness before adding its new constraints. Existing `NeedsReview` rows are
   preserved. Existing `Active`/`Archived` rows are accepted only when the
   repository-defined `LegacyPrivateApproved` Admin audit evidence matches the
   current tenant and participant IDs; state alone is never treated as approval.
   The migration reconstructs a resolved content-free review row from that audit
   evidence without changing the existing access state. It is safe to rerun on an
   unapplied database: canonical roots are inserted with `ON CONFLICT DO NOTHING`,
   and review/audit evidence is inserted only once. It deliberately creates no
   participant mapping from message history.
7. Run `Preflight/202608051210_verify_legacy_private_mapping_gate.sql` after every
   remediation batch. Archive both deterministic result sets. The row report has
   only conversation/tenant IDs and remediation codes/actions; it does not expose
   instruction content. Leave `Messaging:Features:PrivateEnabled=false` until the
   final gate result is `Ready` (zero Invalid and zero NeedsReview). An attempted
   startup with the flag true otherwise fails closed.
8. Run `Preflight/202607261000_verify_case_conversation_readiness.sql`. Resolve every orphan, tenant mismatch, and invalid ticket/inquiry type/category result before continuing. Historical replies may use the reviewed legacy `100` type/category sentinel.
9. Apply `202607261005_normalize_legacy_case_reply_shape.sql` separately. It
   locks instruction writes, repairs only same-tenant canonical case replies whose
   type/category dimensions are either `100` or already match the root, and fails
   closed for every other mismatch. Archive the applied checksum and repaired-row
   notice, then rerun the case readiness preflight; all four integrity result sets
   must return zero rows.
10. Apply `202607261010_modernize_case_conversations.sql` with
   `Attachments:Enabled=false`. Existing canonical ticket and inquiry roots become
   sequence `1`; replies are ordered by
   `COALESCE(datetime, insert_date), id` beginning at `2`; the allocator is
   upserted to `MAX(conversation_sequence) + 1`. The disabled application path
   must keep text history, sends, replay, and outbox delivery operational without
   querying attachment tables because `202607261020` has not run yet.
11. Keep attachments disabled for a documented, uninterrupted 24-hour observation
   gate after the Phase 1 deployment. Verify case history, sends, idempotent
   replay, allocator monotonicity, outbox dispatch, and SignalR delivery. Record
   the gate start/end timestamps and evidence. This repository change does not
   claim that the operational wait has occurred.
12. Apply the ordered attachment schema and forward-fix migrations while the
   feature remains disabled, including
   `202608031000_structural_attachment_validation_mode.sql`. Configure private R2
   and explicitly select `Attachments:SecurityMode=StructuralValidationOnly`.
   Validate the structural worker and record team approval of residual malware
   risk and compensating controls before any separately approved activation.
13. Apply `202608061000_add_persisted_security_stamps.sql` after confirming that
    `pgcrypto.gen_random_bytes(integer)` is installed and that the owners of both
    externally managed identity tables approve the additive columns. Verify every
    row has a 32-byte stamp before enabling the application deployment; rotate
    stamps through the shared service for password/reset, role, compromise, and
    revoke-all events.
14. Apply `202608061100_create_distributed_login_throttling.sql` before enabling
    login traffic on more than one API instance. Grant the application role only
    the table DML required by the throttle repository. The application performs
    bounded stale-row cleanup; do not grant `TRUNCATE` or public access. The
    source window defaults to 20 attempts per minute, while each normalized
    account/source pair receives a five-failure exponential backoff capped at
    fifteen minutes. A successful login clears only that pair's backoff state;
    the source window remains active.
15. Apply `202608071000_create_security_audit_events.sql` before enabling the
    corresponding application build. Provision separate migration/table-owner
    and retention/review roles; never make the runtime role the audit-table
    owner. Verify the append-only trigger and grants, then review audit-write
    failures and retention status operationally. The default audit retention is
    400 days and network context is masked to IPv4 `/24` or IPv6 `/64`.

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

Rollback/forward-fix for the legacy Private gate: once applied, preserve the
review and audit evidence. If the rollout must be halted, keep Private messaging
disabled and use the pre-deployment database backup only under the approved
restore runbook. Do not delete `NeedsReview` rows, resequence instruction history,
or turn the flag on to bypass the gate; correct data with a reviewed forward-fix
and rerun the read-only preflight.

## Empty local migration-validation database

The ordered migration set assumes the externally owned foundational schemas and
tables already exist. It is not a complete production database bootstrap. For a
disposable local validation database only, run
`Bootstrap/local_migration_foundation.sql` in pgAdmin first. The script creates
only local stand-ins for `admin.users`, `internal.clients`,
`internal.support_users`, and `digital.instructions`; it does not create the
migration ledger or any Messaging V2/attachment tables. Run it only against a
new empty local database, then execute the ordered SQL manually through the
reviewed deployment process. Never run this bootstrap against the shared IP
database.

Attachment activation, R2 CORS, worker topology, health degradation, and the
24-hour gate are detailed in
[`Operations/ATTACHMENTS.md`](Operations/ATTACHMENTS.md).
