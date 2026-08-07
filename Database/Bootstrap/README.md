# Local migration-validation bootstrap

`local_migration_foundation.sql` is a disposable local/test setup script. It is
not a production migration and must never be run against the shared IP database.

It creates only the schemas and four foundational table stand-ins required by
the ordered migration set:

- `admin.users`
- `internal.clients`
- `internal.support_users`
- `digital.instructions`

It intentionally creates no data, migration ledger, Messaging V2 tables, case
audit tables, notification tables, attachment tables, or Private review table.
Those are created by the ordered SQL deployment steps.

## Use

1. Create a new empty local PostgreSQL database.
2. Open pgAdmin Query Tool connected to that database.
3. Execute `local_migration_foundation.sql` and allow it to `COMMIT`.
4. From the repository root, inspect the ordered scripts and execute them through
   the reviewed manual deployment process using pgAdmin or psql.

5. Run the read-only preflight SQL under `Database/Preflight`.
6. Archive deployment and preflight evidence; do not create or update
   `digital.schema_migrations` for a new migration.

The production definitions of the externally owned `admin` and `internal`
domains are not stored in this repository. This bootstrap is intentionally a
minimal migration-validation fixture, not an application-ready database
bootstrap.
