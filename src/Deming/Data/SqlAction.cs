using System.Data.Common;
using Microsoft.Data.Sqlite;

namespace Deming.Data;

/// <summary>
/// One call of a named, embedded query. The SQLite counterpart of sd10's MsSqlAction:
/// <code>
/// var task = await db.Sql("Task.Lock")
///     .AddParameter("TaskId", id)          // no '@' prefix
///     .AddParameter("SessionId", session)
///     .GetModelAsync&lt;TaskItem&gt;();
/// </code>
/// Created by <see cref="DemingDb.Sql"/> (own connection, autocommit) or
/// <see cref="DbScope.Sql"/> (inside a BEGIN IMMEDIATE transaction).
/// A violated constraint surfaces as <see cref="ConstraintViolationException"/>; nothing is swallowed.
/// </summary>
public sealed class SqlAction
{
    private readonly DemingDb _db;
    private readonly DbScope? _scope;
    private readonly List<KeyValuePair<string, object?>> _parameters = [];

    internal SqlAction(DemingDb db, DbScope? scope, string name)
    {
        _db = db;
        _scope = scope;
        Name = name;
        Sql = SqlCatalog.Get(name);
    }

    public string Name { get; }
    public string Sql { get; }

    /// <summary>Binds @<paramref name="name"/>. Pass the bare name, without '@'.</summary>
    public SqlAction AddParameter(string name, object? value)
    {
        if (string.IsNullOrWhiteSpace(name) || name[0] is '@' or '$' or ':')
            throw new ArgumentException($"Parameter name '{name}' must be given without a prefix.", nameof(name));
        if (_parameters.Exists(p => string.Equals(p.Key, name, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException($"Parameter '{name}' added twice to {Name}.", nameof(name));
        _parameters.Add(new(name, value));
        return this;
    }

    /// <summary>Binds every public property of <paramref name="model"/> as @PropertyName.</summary>
    public SqlAction AddParameters(object model)
    {
        ArgumentNullException.ThrowIfNull(model);
        foreach (var (name, value) in ModelMapper.ParametersOf(model))
            AddParameter(name, value);
        return this;
    }

    /// <summary>First row mapped to <typeparamref name="T"/>, or null when there is no row.</summary>
    public Task<T?> GetModelAsync<T>(CancellationToken ct = default) where T : class, new() =>
        RunAsync(async cmd =>
        {
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
                return null;
            return ModelMapper.For<T>(reader)(reader);
        }, ct);

    /// <summary>All rows mapped to <typeparamref name="T"/>.</summary>
    public Task<List<T>> GetModelListAsync<T>(CancellationToken ct = default) where T : class, new() =>
        RunAsync(async cmd =>
        {
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            var list = new List<T>();
            Func<DbDataReader, T>? map = null;
            while (await reader.ReadAsync(ct))
            {
                map ??= ModelMapper.For<T>(reader);
                list.Add(map(reader));
            }
            return list;
        }, ct);

    /// <summary>First column of the first row (count, id, flag...), or default when there is no row.</summary>
    public Task<T?> GetScalarAsync<T>(CancellationToken ct = default) =>
        RunAsync(async cmd =>
        {
            if (!SqlValue.IsScalar(typeof(T)))
                throw new InvalidOperationException($"{typeof(T).Name} is not a scalar type; use GetModelAsync.");
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
                return default;
            return (T?)SqlValue.FromColumn(reader.GetValue(0), typeof(T), reader.GetName(0));
        }, ct);

    /// <summary>Executes the statement(s); returns the number of rows changed.</summary>
    public Task<int> ExecuteAsync(CancellationToken ct = default) =>
        RunAsync(cmd => cmd.ExecuteNonQueryAsync(ct), ct);

    private async Task<TResult> RunAsync<TResult>(Func<SqliteCommand, Task<TResult>> body, CancellationToken ct)
    {
        SqliteConnection? owned = null;
        try
        {
            var connection = _scope?.Connection ?? (owned = await _db.OpenConnectionAsync(ct));
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = Sql;
            cmd.Transaction = _scope?.Transaction;
            foreach (var (name, value) in _parameters)
                cmd.Parameters.AddWithValue("@" + name, SqlValue.ToParameter(value));

            return await body(cmd);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == ConstraintViolationException.SqliteConstraint)
        {
            throw ConstraintViolationException.From(Name, ex);
        }
        finally
        {
            if (owned is not null)
                await owned.DisposeAsync();
        }
    }
}
