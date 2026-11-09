using System.Globalization;

namespace Deming.Data;

/// <summary>
/// The only place where CLR values and SQLite storage values are converted.
/// SQLite stores INTEGER (long), REAL (double), TEXT (string), BLOB (byte[]) and NULL.
/// </summary>
internal static class SqlValue
{
    /// <summary>Timestamp text format; identical to strftime('%Y-%m-%dT%H:%M:%fZ') in the schema.</summary>
    public const string TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";

    public static string FormatTimestamp(DateTime value)
    {
        var utc = value.Kind switch
        {
            DateTimeKind.Local => value.ToUniversalTime(),
            // Unspecified is treated as UTC: the database never holds local times.
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        };
        return utc.ToString(TimestampFormat, CultureInfo.InvariantCulture);
    }

    public static object ToParameter(object? value) => value switch
    {
        null => DBNull.Value,
        DateTime dt => FormatTimestamp(dt),
        DateTimeOffset dto => FormatTimestamp(dto.UtcDateTime),
        bool b => b ? 1L : 0L,
        Guid g => g.ToString("D"),
        Enum e => e.ToString(),
        _ => value,
    };

    public static object? FromColumn(object? raw, Type target, string column)
    {
        var underlying = Nullable.GetUnderlyingType(target);

        if (raw is null || raw is DBNull)
        {
            if (target.IsValueType && underlying is null)
                throw new InvalidOperationException(
                    $"Column '{column}' is NULL but the target type {target.Name} is not nullable.");
            return null;
        }

        var type = underlying ?? target;

        if (type.IsInstanceOfType(raw))
            return raw;

        if (type == typeof(string))
            return Convert.ToString(raw, CultureInfo.InvariantCulture);
        if (type == typeof(bool))
            return Convert.ToInt64(raw, CultureInfo.InvariantCulture) != 0;
        if (type == typeof(DateTime))
            return DateTime.Parse((string)raw, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
        if (type == typeof(DateTimeOffset))
            return DateTimeOffset.Parse((string)raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
        if (type == typeof(Guid))
            return Guid.Parse((string)raw);
        if (type.IsEnum)
            return raw is string s
                ? Enum.Parse(type, s, ignoreCase: true)
                : Enum.ToObject(type, Convert.ToInt64(raw, CultureInfo.InvariantCulture));

        return Convert.ChangeType(raw, type, CultureInfo.InvariantCulture);
    }

    public static bool IsScalar(Type type)
    {
        var t = Nullable.GetUnderlyingType(type) ?? type;
        return t.IsPrimitive || t.IsEnum || t == typeof(string) || t == typeof(decimal)
            || t == typeof(DateTime) || t == typeof(DateTimeOffset) || t == typeof(Guid) || t == typeof(byte[]);
    }
}
