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
Those are created by `CBSSupport.DatabaseMigrator`.

## Use

1. Create a new empty local PostgreSQL database.
2. Open pgAdmin Query Tool connected to that database.
3. Execute `local_migration_foundation.sql` and allow it to `COMMIT`.
4. From the repository root, run the approved migrator:

```powershell
$env:CBSSUPPORT_MIGRATIONS_CONNECTION = '<local connection string>'
dotnet run --project .\CBSSupport.DatabaseMigrator\CBSSupport.DatabaseMigrator.csproj -- --dry-run
dotnet run --project .\CBSSupport.DatabaseMigrator\CBSSupport.DatabaseMigrator.csproj -- --applied-by $env:USERNAME
```

5. Run the read-only preflight SQL under `Database/Preflight`.
6. Run the migrator again and run the preflight again to verify idempotency.
7. Clear the connection-string environment variable when finished.

The production definitions of the externally owned `admin` and `internal`
domains are not stored in this repository. This bootstrap is intentionally a
minimal migration-validation fixture, not an application-ready database
bootstrap.
