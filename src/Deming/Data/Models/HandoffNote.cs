namespace Deming.Data.Models;

/// <summary>Row of table <c>handoff_note</c>.</summary>
public sealed class HandoffNote
{
    public long Id { get; set; }
    public long TaskId { get; set; }
    public string Kind { get; set; } = HandoffNoteKind.Handoff;
    public string Summary { get; set; } = "";
    public string? Caveats { get; set; }
    public string? TouchedFiles { get; set; }
    public string? CommitHash { get; set; }
    public string? SessionId { get; set; }
    public string? TakenOverFrom { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>Values of handoff_note.kind (CHECK ck_handoff_note_kind).</summary>
public static class HandoffNoteKind
{
    public const string Handoff = "handoff";
    public const string Note = "note";
}

/// <summary>
/// Field caps (scope § 5). The instruction states <c>*Target</c>; the database accepts
/// <c>*Max</c> (target + 10 %). Keep these in sync with the CHECKs in 0001_initial.sql.
/// </summary>
public static class NoteLimits
{
    public const int SummaryTarget = 800;
    public const int CaveatsTarget = 800;
    public const int TouchedFilesTarget = 1500;
    public const int DecisionTextTarget = 400;

    /// <summary>Findings: title 200; body target 10000 (longest of 27 Harness findings: 7543), database accepts +10 % = 11000.</summary>
    public const int FindingTitleMax = 200;
    public const int FindingBodyTarget = 10000;
    public const int FindingBodyMax = 11000;

    public const int SummaryMax = 880;
    public const int CaveatsMax = 880;
    public const int TouchedFilesMax = 1650;
    public const int DecisionTextMax = 440;
}
