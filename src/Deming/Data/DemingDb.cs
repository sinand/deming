using Microsoft.Data.Sqlite;

namespace Deming.Data;

/// <summary>
/// The Deming database: one SQLite file in WAL mode. Singleton; connections are pooled by
/// Microsoft.Data.Sqlite. Every connection has foreign keys on and deming_normalize() registered.
/// </summary>
public sealed class DemingDb
{
    public DemingDb(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        DatabasePath = Path.GetFullPath(databasePath);
        ConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            ForeignKeys = true,
            Pooling = true,
            // Seconds a command waits on a locked database (SQLITE_BUSY) before failing.
            DefaultTimeout = 30,
        }.ToString();
    }

    public string DatabasePath { get; }
    public string ConnectionString { get; }

    /// <summary>A named query on its own connection (autocommit).</summary>
    public SqlAction Sql(string name) => new(this, null, name);

    public async Task<SqliteConnection> OpenConnectionAsync(CancellationToken ct = default)
    {
        var directory = Path.GetDirectoryName(DatabasePath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var connection = new SqliteConnection(ConnectionString);
        try
        {
            await connection.OpenAsync(ct);
            connection.CreateFunction<string?, string?>(
                TextNormalizer.SqlFunctionName, TextNormalizer.NormalizeOrNull, isDeterministic: true);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// Runs <paramref name="work"/> in one BEGIN IMMEDIATE transaction: the write lock is taken
    /// up front, so two writers never deadlock on lock upgrade. Commits when the delegate
    /// returns, rolls back when it throws. To abort on a business rule, throw.
    /// </summary>
    public async Task<T> InTransactionAsync<T>(Func<DbScope, Task<T>> work, CancellationToken ct = default)
    {
        await using var connection = await OpenConnectionAsync(ct);
        // deferred: false -> "BEGIN IMMEDIATE".
        await using var transaction = connection.BeginTransaction(deferred: false);
        var scope = new DbScope(this, connection, transaction);
        try
        {
            var result = await work(scope);
            await transaction.CommitAsync(ct);
            return result;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public Task InTransactionAsync(Func<DbScope, Task> work, CancellationToken ct = default) =>
        InTransactionAsync<bool>(async scope =>
        {
            await work(scope);
            return true;
        }, ct);

    /// <summary>Applies missing schema scripts. Called by "deming serve" at startup.</summary>
    public Task<int> MigrateAsync(CancellationToken ct = default) => SchemaMigrator.MigrateAsync(this, ct);

    /// <summary>Default location: &lt;LocalApplicationData&gt;/Deming/deming.db
    /// (Windows %LOCALAPPDATA%, Linux $XDG_DATA_HOME or ~/.local/share, macOS ~/Library/Application Support).</summary>
    public static string DefaultPath() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create),
            "Deming",
            "deming.db");
}

/// <summary>A connection with an open transaction; queries created here run inside it.</summary>
public sealed class DbScope
{
    private readonly DemingDb _db;

    internal DbScope(DemingDb db, SqliteConnection connection, SqliteTransaction transaction)
    {
        _db = db;
        Connection = connection;
        Transaction = transaction;
    }

    internal SqliteConnection Connection { get; }
    internal SqliteTransaction Transaction { get; }

    public SqlAction Sql(string name) => new(_db, this, name);
}
