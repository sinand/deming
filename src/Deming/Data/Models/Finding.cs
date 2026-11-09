namespace Deming.Data.Models;

/// <summary>Row of table <c>finding</c>: a measurement or observation that is not a decision.</summary>
public sealed class Finding
{
    public long Id { get; set; }
    public long ProjectId { get; set; }
    public long? TaskId { get; set; }
    public long? DecisionId { get; set; }
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
    public string? SessionId { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>A row of the finding list endpoint (Sql/Finding/List.sql): no body, only its length.</summary>
public class FindingListItem
{
    public long Id { get; set; }
    public string Title { get; set; } = "";
    public long BodyLength { get; set; }
    public long? TaskId { get; set; }
    public long? DecisionId { get; set; }
    public string? SessionId { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>A finding of the cross-project list (Sql/Finding/ListAll.sql): a list row plus its project.</summary>
public sealed class FindingOverviewItem : FindingListItem
{
    public long ProjectId { get; set; }
    public string ProjectName { get; set; } = "";
    public bool ProjectIsActive { get; set; } = true;
}
