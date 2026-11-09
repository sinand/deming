using Deming.Data;
using Microsoft.Data.Sqlite;

namespace Deming.Tests;

/// <summary>
/// A fresh SQLite file per test, migrated to the current schema; deleted on dispose.
/// Tests never share a database.
/// </summary>
public sealed class TempDatabase : IAsyncDisposable
{
    private TempDatabase(string path)
    {
        Path = path;
        Db = new DemingDb(path);
    }

    public string Path { get; }
    public DemingDb Db { get; }

    public static string NewPath() =>
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "deming-tests", Guid.NewGuid().ToString("N") + ".db");

    public static async Task<TempDatabase> CreateAsync(bool migrate = true)
    {
        var db = new TempDatabase(NewPath());
        if (migrate)
            await db.Db.MigrateAsync(TestContext.Current.CancellationToken);
        return db;
    }

    /// <summary>Raw SQL for tests that probe constraints directly (production code uses named queries).</summary>
    public async Task<int> ExecAsync(string sql)
    {
        await using var c = await Db.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        try
        {
            return await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            throw ConstraintViolationException.From("test", ex);
        }
    }

    public async Task<object?> ScalarAsync(string sql)
    {
        await using var c = await Db.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        DeleteFiles(Path, Db.ConnectionString);
        return ValueTask.CompletedTask;
    }

    public static void DeleteFiles(string path, string connectionString)
    {
        using (var c = new SqliteConnection(connectionString))
            SqliteConnection.ClearPool(c);
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try { File.Delete(path + suffix); }
            catch (IOException) { /* best effort; temp dir */ }
        }
    }
}
