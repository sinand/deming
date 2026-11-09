namespace Deming.Data.Models;

/// <summary>Where a note in a `next` / `take` response comes from.</summary>
public static class NextNoteSource
{
    /// <summary>Note of a task the returned task depends on.</summary>
    public const string Dependency = "dependency";

    /// <summary>Note of one of the project's last finished tasks.</summary>
    public const string Recent = "recent";
}

/// <summary>A handoff note as `next` returns it (projection of Sql/HandoffNote/ForNext.sql).</summary>
public sealed class NextNote
{
    public string Source { get; set; } = "";
    public long TaskId { get; set; }
    public string TaskTitle { get; set; } = "";
    public string Kind { get; set; } = HandoffNoteKind.Handoff;
    public string Summary { get; set; } = "";
    public string? Caveats { get; set; }
    public string? TouchedFiles { get; set; }
    public string? CommitHash { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>An active decision as `next` returns it: no rationale, text cut at <see cref="NextLimits.DecisionText"/>.</summary>
public sealed class NextDecision
{
    public long Id { get; set; }
    public string Title { get; set; } = "";
    public string Text { get; set; } = "";
    public bool Truncated { get; set; }
    public long? TaskId { get; set; }
    public DateTime CreatedAt { get; set; }
}

public static class NextLimits
{
    /// <summary>Decision text length in `next` (scope § 5).</summary>
    public const int DecisionText = 250;
}
