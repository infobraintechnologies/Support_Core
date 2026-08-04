using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Npgsql;

const string connectionEnvironmentVariable = "CBSSUPPORT_MIGRATIONS_CONNECTION";
const string advisoryLockName = "cbssupport:database-migrations";
const string ledgerTable = "digital.schema_migrations";

var options = MigrationOptions.Parse(args);
var scripts = MigrationScript.LoadAll(options.MigrationsDirectory);

if (options.DryRun)
{
    Console.WriteLine("Dry run: no database connection will be made.");
    foreach (var script in scripts)
    {
        Console.WriteLine($"{script.Version} {script.Checksum}");
    }

    return;
}

var connectionString = Environment.GetEnvironmentVariable(connectionEnvironmentVariable);
if (string.IsNullOrWhiteSpace(connectionString))
{
    throw new InvalidOperationException(
        $"Set the {connectionEnvironmentVariable} environment variable before running migrations.");
}

await using var connection = new NpgsqlConnection(connectionString);
await connection.OpenAsync();

if (!await TryAcquireAdvisoryLockAsync(connection))
{
    throw new InvalidOperationException("Another CBS Support migration process is already running.");
}

try
{
    var appliedMigrations = await LoadAppliedMigrationsAsync(connection);
    if (await LedgerExistsAsync(connection)
        && !appliedMigrations.ContainsKey(scripts[0].Version))
    {
        throw new InvalidOperationException(
            $"{ledgerTable} exists but does not record the bootstrap migration " +
            $"{scripts[0].Version}. Do not continue from a manually-created partial ledger.");
    }

    foreach (var script in scripts)
    {
        if (appliedMigrations.TryGetValue(script.Version, out var appliedChecksum))
        {
            if (!string.Equals(appliedChecksum, script.Checksum, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Applied migration {script.Version} has a different checksum. " +
                    "Applied migration files are immutable; create a forward-fix migration instead.");
            }

            Console.WriteLine($"Skipped {script.Version}; already applied.");
            continue;
        }

        var stopwatch = Stopwatch.StartNew();
        if (script.IsTransactional)
        {
            await ApplyTransactionalMigrationAsync(connection, script, options.AppliedBy, stopwatch);
        }
        else
        {
            await ApplyNonTransactionalMigrationAsync(connection, script, options.AppliedBy, stopwatch);
        }

        appliedMigrations.Add(script.Version, script.Checksum);
        Console.WriteLine($"Applied {script.Version} in {stopwatch.ElapsedMilliseconds} ms.");
    }
}
finally
{
    await ReleaseAdvisoryLockAsync(connection);
}

static async Task ApplyTransactionalMigrationAsync(
    NpgsqlConnection connection,
    MigrationScript script,
    string appliedBy,
    Stopwatch stopwatch)
{
    await using var transaction = await connection.BeginTransactionAsync();
    await ExecuteSqlAsync(connection, script.Sql, transaction);
    stopwatch.Stop();
    await InsertLedgerEntryAsync(connection, script, appliedBy, stopwatch.ElapsedMilliseconds, transaction);
    await transaction.CommitAsync();
}

static async Task ApplyNonTransactionalMigrationAsync(
    NpgsqlConnection connection,
    MigrationScript script,
    string appliedBy,
    Stopwatch stopwatch)
{
    await ExecuteSqlAsync(connection, script.Sql, transaction: null);
    stopwatch.Stop();

    await using var transaction = await connection.BeginTransactionAsync();
    await InsertLedgerEntryAsync(connection, script, appliedBy, stopwatch.ElapsedMilliseconds, transaction);
    await transaction.CommitAsync();
}

static async Task ExecuteSqlAsync(
    NpgsqlConnection connection,
    string sql,
    NpgsqlTransaction? transaction)
{
    foreach (var commandText in SplitMigrationCommands(sql))
    {
        await using var command = new NpgsqlCommand(commandText, connection, transaction);
        await command.ExecuteNonQueryAsync();
    }
}

static IEnumerable<string> SplitMigrationCommands(string sql)
{
    const string commandBoundary = "-- migration-command-boundary";

    return sql
        .Split(commandBoundary, StringSplitOptions.RemoveEmptyEntries)
        .Select(command => command.Trim())
        .Where(command => command.Length > 0);
}

static async Task InsertLedgerEntryAsync(
    NpgsqlConnection connection,
    MigrationScript script,
    string appliedBy,
    long executionMilliseconds,
    NpgsqlTransaction transaction)
{
    const string sql = """
        INSERT INTO digital.schema_migrations (version, checksum, applied_by, execution_ms)
        VALUES (@version, @checksum, @appliedBy, @executionMs);
        """;

    await using var command = new NpgsqlCommand(sql, connection, transaction);
    command.Parameters.AddWithValue("version", script.Version);
    command.Parameters.AddWithValue("checksum", script.Checksum);
    command.Parameters.AddWithValue("appliedBy", appliedBy);
    command.Parameters.AddWithValue("executionMs", checked((int)executionMilliseconds));
    await command.ExecuteNonQueryAsync();
}

static async Task<Dictionary<string, string>> LoadAppliedMigrationsAsync(NpgsqlConnection connection)
{
    if (!await LedgerExistsAsync(connection))
    {
        return new Dictionary<string, string>(StringComparer.Ordinal);
    }

    const string sql = """
        SELECT version, checksum
        FROM digital.schema_migrations
        ORDER BY version;
        """;
    await using var command = new NpgsqlCommand(sql, connection);
    await using var reader = await command.ExecuteReaderAsync();
    var migrations = new Dictionary<string, string>(StringComparer.Ordinal);
    while (await reader.ReadAsync())
    {
        migrations.Add(reader.GetString(0), reader.GetString(1));
    }

    return migrations;
}

static async Task<bool> LedgerExistsAsync(NpgsqlConnection connection)
{
    const string sql = "SELECT to_regclass('digital.schema_migrations') IS NOT NULL;";
    await using var command = new NpgsqlCommand(sql, connection);
    return (bool)(await command.ExecuteScalarAsync() ?? false);
}

static async Task<bool> TryAcquireAdvisoryLockAsync(NpgsqlConnection connection)
{
    const string sql = "SELECT pg_try_advisory_lock(hashtext(@lockName));";
    await using var command = new NpgsqlCommand(sql, connection);
    command.Parameters.AddWithValue("lockName", advisoryLockName);
    return (bool)(await command.ExecuteScalarAsync() ?? false);
}

static async Task ReleaseAdvisoryLockAsync(NpgsqlConnection connection)
{
    const string sql = "SELECT pg_advisory_unlock(hashtext(@lockName));";
    await using var command = new NpgsqlCommand(sql, connection);
    command.Parameters.AddWithValue("lockName", advisoryLockName);
    await command.ExecuteNonQueryAsync();
}

sealed record MigrationScript(string Version, string Sql, string Checksum, bool IsTransactional)
{
    private const string TransactionDirective = "-- migration-transaction:";

    public static IReadOnlyList<MigrationScript> LoadAll(string migrationsDirectory)
    {
        if (!Directory.Exists(migrationsDirectory))
        {
            throw new DirectoryNotFoundException($"Migration directory was not found: {migrationsDirectory}");
        }

        var scripts = Directory.EnumerateFiles(migrationsDirectory, "*.sql", SearchOption.TopDirectoryOnly)
            .OrderBy(Path.GetFileName, StringComparer.Ordinal)
            .Select(Load)
            .ToArray();

        if (scripts.Length == 0)
        {
            throw new InvalidOperationException($"No migration scripts were found in {migrationsDirectory}.");
        }

        return scripts;
    }

    private static MigrationScript Load(string path)
    {
        var version = Path.GetFileNameWithoutExtension(path);
        if (!System.Text.RegularExpressions.Regex.IsMatch(version, "^\\d{12}_[a-z0-9_]+$"))
        {
            throw new InvalidOperationException(
                $"Migration filename must match YYYYMMDDHHMM_short_description.sql: {Path.GetFileName(path)}");
        }

        var sql = File.ReadAllText(path, Encoding.UTF8);
        if (sql.Contains("BEGIN;", StringComparison.OrdinalIgnoreCase)
            || sql.Contains("COMMIT;", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Migration {version} contains transaction control. The runner owns the transaction.");
        }

        var directive = sql.Split('\n').Take(20)
            .FirstOrDefault(line => line.TrimStart().StartsWith(TransactionDirective, StringComparison.OrdinalIgnoreCase));
        var isTransactional = directive is null
            || !directive[(directive.IndexOf(':') + 1)..].Trim().Equals("false", StringComparison.OrdinalIgnoreCase);

        return new MigrationScript(
            version,
            sql,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sql))).ToLowerInvariant(),
            isTransactional);
    }
}

sealed record MigrationOptions(string MigrationsDirectory, string AppliedBy, bool DryRun)
{
    public static MigrationOptions Parse(string[] args)
    {
        var migrationsDirectory = Path.Combine(AppContext.BaseDirectory, "Migrations");
        var appliedBy = Environment.UserName;
        var dryRun = false;

        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--dry-run":
                    dryRun = true;
                    break;
                case "--migrations-dir" when index + 1 < args.Length:
                    migrationsDirectory = Path.GetFullPath(args[++index]);
                    break;
                case "--applied-by" when index + 1 < args.Length:
                    appliedBy = args[++index];
                    break;
                default:
                    throw new ArgumentException(
                        "Usage: --dry-run | --migrations-dir <path> | --applied-by <name>");
            }
        }

        if (string.IsNullOrWhiteSpace(appliedBy))
        {
            throw new ArgumentException("The applied-by value cannot be empty.");
        }

        return new MigrationOptions(migrationsDirectory, appliedBy, dryRun);
    }
}
