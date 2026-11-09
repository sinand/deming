using Microsoft.Data.Sqlite;

namespace Deming.Data;

/// <summary>
/// Applies Data/Schema/NNNN_*.sql in order, each in its own BEGIN IMMEDIATE transaction
/// together with its schema_version row. Applied scripts are never edited: a change is a new
/// numbered script. A database newer than the binary is refused, not silently used.
/// </summary>
internal static class SchemaMigrator
{
    private const string CreateVersionTable = """
        CREATE TABLE IF NOT EXISTS schema_version (
            version    INTEGER PRIMARY KEY,
            name       TEXT NOT NULL,
            applied_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now'))
        );
        """;

    public static async Task<int> MigrateAsync(DemingDb db, CancellationToken ct)
    {
        var scripts = SqlCatalog.Schema;
        await using var connection = await db.OpenConnectionAsync(ct);

        // WAL is a property of the file; set once, it persists.
        var mode = await ScalarAsync(connection, null, "PRAGMA journal_mode = WAL;", ct);
        if (!string.Equals(mode as string, "wal", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Could not switch {db.DatabasePath} to WAL mode (got '{mode}').");

        await ExecAsync(connection, null, CreateVersionTable, ct);

        var installed = await CurrentVersionAsync(connection, null, ct);
        if (installed > scripts.Count)
            throw new InvalidOperationException(
                $"Database {db.DatabasePath} is at schema version {installed}, this binary knows up to {scripts.Count}. Use a newer Deming.");

        var applied = 0;
        foreach (var script in scripts)
        {
            if (script.Version <= installed)
                continue;

            await using var tx = connection.BeginTransaction(deferred: false);
            // Re-read inside the write lock: another process may have migrated meanwhile.
            if (script.Version <= await CurrentVersionAsync(connection, tx, ct))
            {
                await tx.RollbackAsync(ct);
                continue;
            }

            await ExecAsync(connection, tx, script.Sql, ct);
            await using (var insert = connection.CreateCommand())
            {
                insert.Transaction = tx;
                insert.CommandText = "INSERT INTO schema_version (version, name) VALUES (@version, @name);";
                insert.Parameters.AddWithValue("@version", script.Version);
                insert.Parameters.AddWithValue("@name", script.Name);
                await insert.ExecuteNonQueryAsync(ct);
            }
            await tx.CommitAsync(ct);
            applied++;
        }

        return applied;
    }

    private static async Task<int> CurrentVersionAsync(SqliteConnection c, SqliteTransaction? tx, CancellationToken ct) =>
        Convert.ToInt32(await ScalarAsync(c, tx, "SELECT coalesce(max(version), 0) FROM schema_version;", ct));

    private static async Task ExecAsync(SqliteConnection c, SqliteTransaction? tx, string sql, CancellationToken ct)
    {
        await using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<object?> ScalarAsync(SqliteConnection c, SqliteTransaction? tx, string sql, CancellationToken ct)
    {
        await using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        return await cmd.ExecuteScalarAsync(ct);
    }
}
