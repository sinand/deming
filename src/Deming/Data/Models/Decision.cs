namespace Deming.Data.Models;

/// <summary>Row of table <c>decision</c>.</summary>
public sealed class Decision
{
    public long Id { get; set; }
    public long ProjectId { get; set; }
    public long? TaskId { get; set; }
    public string Title { get; set; } = "";
    public string Text { get; set; } = "";
    public string? Rationale { get; set; }
    public string Status { get; set; } = DecisionStatus.Active;
    public long? SupersededById { get; set; }
    public DateTime? SupersededAt { get; set; }
    public string? SessionId { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>Values of decision.status (CHECK ck_decision_status).</summary>
public static class DecisionStatus
{
    public const string Active = "active";
    public const string Superseded = "superseded";
}
