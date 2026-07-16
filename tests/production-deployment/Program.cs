using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

if (args.Length != 1)
{
    Console.Error.WriteLine("Usage: production-deployment-harness <extraction-commerce.db>");
    return 2;
}

var databasePath = Path.GetFullPath(args[0]);
if (!File.Exists(databasePath))
{
    throw new FileNotFoundException("The startup migration database was not created.", databasePath);
}

var builder = new SqliteConnectionStringBuilder
{
    DataSource = databasePath,
    Mode = SqliteOpenMode.ReadOnly,
    Cache = SqliteCacheMode.Private
};
await using var connection = new SqliteConnection(builder.ConnectionString);
await connection.OpenAsync();

await using (var integrity = connection.CreateCommand())
{
    integrity.CommandText = "PRAGMA integrity_check;";
    var result = Convert.ToString(await integrity.ExecuteScalarAsync());
    if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
    {
        throw new InvalidDataException($"SQLite integrity_check failed: {result}");
    }
}

await using (var foreignKeys = connection.CreateCommand())
{
    foreignKeys.CommandText = "PRAGMA foreign_key_check;";
    await using var reader = await foreignKeys.ExecuteReaderAsync();
    if (await reader.ReadAsync())
    {
        throw new InvalidDataException("SQLite foreign_key_check returned a violation.");
    }
}

var requiredTables = new[]
{
    "extraction_events",
    "extraction_settlement_runs",
    "economy_schema_migrations",
    "paldefender_commands",
    "content_schema_migrations",
    "reliable_task_schema"
};
foreach (var table in requiredTables)
{
    await using var command = connection.CreateCommand();
    command.CommandText = """
        SELECT COUNT(*)
        FROM sqlite_master
        WHERE type = 'table' AND name = $name;
        """;
    command.Parameters.AddWithValue("$name", table);
    if (Convert.ToInt64(await command.ExecuteScalarAsync()) != 1)
    {
        throw new InvalidDataException($"Required migrated table '{table}' is missing.");
    }
}

await using (var version = connection.CreateCommand())
{
    version.CommandText = "PRAGMA user_version;";
    if (Convert.ToInt64(await version.ExecuteScalarAsync()) != 1)
    {
        throw new InvalidDataException("Unexpected SQLite user_version after startup migration.");
    }
}

var evidence = new List<string>();
await ReadRowsAsync(
    connection,
    "SELECT component, version FROM economy_schema_migrations ORDER BY component;",
    evidence);
await ReadRowsAsync(
    connection,
    "SELECT component, version FROM content_schema_migrations ORDER BY component;",
    evidence);
await ReadRowsAsync(
    connection,
    "SELECT component, version FROM reliable_task_schema ORDER BY component;",
    evidence);

if (evidence.Count < 4)
{
    throw new InvalidDataException("Startup did not record the expected component migrations.");
}

var canonical = string.Join('\n', evidence);
var fingerprint = Convert.ToHexString(
    SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
Console.WriteLine(JsonSerializer.Serialize(new
{
    databasePath,
    migrationCount = evidence.Count,
    migrationFingerprint = fingerprint
}));
return 0;

static async Task ReadRowsAsync(
    SqliteConnection connection,
    string sql,
    List<string> destination)
{
    await using var command = connection.CreateCommand();
    command.CommandText = sql;
    await using var reader = await command.ExecuteReaderAsync();
    var seen = new HashSet<string>(StringComparer.Ordinal);
    while (await reader.ReadAsync())
    {
        var row = $"{reader.GetString(0)}:{reader.GetInt64(1)}";
        if (!seen.Add(row))
        {
            throw new InvalidDataException($"Duplicate migration row '{row}'.");
        }
        destination.Add(row);
    }
}
