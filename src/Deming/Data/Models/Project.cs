namespace Deming.Data.Models;

/// <summary>Row of table <c>project</c>.</summary>
public class Project
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string NameNormalized { get; set; } = "";
    public string? Description { get; set; }

    /// <summary>Where the repository lives (where <c>deming init</c> wrote .deming); informational.</summary>
    public string? RepoPath { get; set; }

    /// <summary>An inactive project is kept with its history but takes no next/take/add.</summary>
    public bool IsActive { get; set; } = true;

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>A row of the project list (Sql/Project/List.sql): the project and its task counts.</summary>
public sealed class ProjectSummary : Project
{
    /// <summary>Tasks that are not cancelled.</summary>
    public int TotalTasks { get; set; }

    public int DoneTasks { get; set; }

    /// <summary>Tasks waiting in the queue.</summary>
    public int QueuedTasks { get; set; }
}
