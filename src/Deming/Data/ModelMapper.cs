using System.Collections.Concurrent;
using System.ComponentModel.DataAnnotations.Schema;
using System.Data.Common;
using System.Reflection;

namespace Deming.Data;

/// <summary>
/// Maps result columns to properties. No hand-written mapping:
///   * a property marked [Column("x")] receives column x;
///   * otherwise snake_case columns match PascalCase properties (lock_owner -> LockOwner);
///   * [NotMapped] properties and columns without a property are skipped.
/// </summary>
internal static class ModelMapper
{
    private static readonly ConcurrentDictionary<Type, Dictionary<string, PropertyInfo>> Cache = new();

    public static string Key(string name) => name.Replace("_", "", StringComparison.Ordinal).ToLowerInvariant();

    private static Dictionary<string, PropertyInfo> PropertiesOf(Type type) => Cache.GetOrAdd(type, static t =>
    {
        var map = new Dictionary<string, PropertyInfo>();
        foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!p.CanWrite || p.GetIndexParameters().Length > 0 || p.IsDefined(typeof(NotMappedAttribute)))
                continue;
            var name = p.GetCustomAttribute<ColumnAttribute>()?.Name ?? p.Name;
            if (!map.TryAdd(Key(name), p))
                throw new InvalidOperationException($"{t.Name}: two properties map to column '{name}'.");
        }
        return map;
    });

    /// <summary>Builds a row reader for the current result set of <paramref name="reader"/>.</summary>
    public static Func<DbDataReader, T> For<T>(DbDataReader reader) where T : new()
    {
        var props = PropertiesOf(typeof(T));
        var bindings = new List<(int Ordinal, PropertyInfo Property, string Column)>();
        for (var i = 0; i < reader.FieldCount; i++)
        {
            var column = reader.GetName(i);
            if (props.TryGetValue(Key(column), out var p))
                bindings.Add((i, p, column));
        }

        return r =>
        {
            var model = new T();
            foreach (var (ordinal, property, column) in bindings)
                property.SetValue(model, SqlValue.FromColumn(r.GetValue(ordinal), property.PropertyType, column));
            return model;
        };
    }

    /// <summary>Readable public properties, used to bind a model as parameters (@PropertyName).</summary>
    public static IEnumerable<(string Name, object? Value)> ParametersOf(object model)
    {
        foreach (var p in model.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!p.CanRead || p.GetIndexParameters().Length > 0 || p.IsDefined(typeof(NotMappedAttribute)))
                continue;
            yield return (p.Name, p.GetValue(model));
        }
    }
}
