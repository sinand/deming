using Microsoft.Data.Sqlite;

namespace Deming.Data;

public enum ConstraintKind
{
    Other,
    Unique,
    PrimaryKey,
    Check,
    NotNull,
    ForeignKey,
    /// <summary>RAISE(ABORT, 'code') from a trigger; <see cref="ConstraintViolationException.Constraint"/> is the code.</summary>
    Trigger,
}

/// <summary>
/// A rule enforced by the database was violated (SQLITE_CONSTRAINT). Services map it to an
/// API error by <see cref="Kind"/> and <see cref="Constraint"/>:
///   Check   -> constraint name, e.g. "ck_task_lock_only_in_progress"
///   Unique  -> "table.col, table.col" or "index 'ux_name'" (SQLite's wording)
///   Trigger -> the code given to RAISE, e.g. "dependency_cycle"
/// </summary>
public sealed class ConstraintViolationException : Exception
{
    public ConstraintViolationException(string query, ConstraintKind kind, string constraint, SqliteException inner)
        : base($"{query}: {kind} constraint violated: {constraint}", inner)
    {
        Query = query;
        Kind = kind;
        Constraint = constraint;
    }

    public string Query { get; }
    public ConstraintKind Kind { get; }
    public string Constraint { get; }

    public bool Is(ConstraintKind kind, string constraint) =>
        Kind == kind && Constraint.Contains(constraint, StringComparison.Ordinal);

    internal const int SqliteConstraint = 19;

    internal static ConstraintViolationException From(string query, SqliteException ex)
    {
        var kind = ex.SqliteExtendedErrorCode switch
        {
            2067 => ConstraintKind.Unique,
            1555 => ConstraintKind.PrimaryKey,
            275 => ConstraintKind.Check,
            1299 => ConstraintKind.NotNull,
            787 => ConstraintKind.ForeignKey,
            1811 => ConstraintKind.Trigger,
            _ => ConstraintKind.Other,
        };

        // Message shape: "SQLite Error 19: 'CHECK constraint failed: ck_name'."
        var message = ex.Message;
        var start = message.IndexOf('\'');
        var end = message.LastIndexOf('\'');
        var detail = start >= 0 && end > start ? message[(start + 1)..end] : message;
        var colon = detail.IndexOf("constraint failed: ", StringComparison.Ordinal);
        if (colon >= 0)
            detail = detail[(colon + "constraint failed: ".Length)..];

        return new ConstraintViolationException(query, kind, detail, ex);
    }
}
