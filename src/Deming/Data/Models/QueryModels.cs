namespace Deming.Data.Models;

/// <summary>A row of the task list endpoint (Sql/Task/List.sql): never carries the scope.</summary>
public class TaskListItem
{
    public long SortOrder { get; set; }
    public long Id { get; set; }
    public string Title { get; set; } = "";
    public string Status { get; set; } = TaskItemStatus.Queued;
    public int Priority { get; set; } = TaskPriority.Default;
    public string Source { get; set; } = TaskSource.Agent;
    public int OpenDependencyCount { get; set; }
    public string? LockOwner { get; set; }

    /// <summary>When the lock was taken or last refreshed; lets a reader tell a stale lock from a live one.</summary>
    public DateTime? LockedAt { get; set; }
}

/// <summary>A task another task waits for (Sql/Task/Dependencies.sql).</summary>
public sealed class TaskDependencyInfo
{
    public long Id { get; set; }
    public string Title { get; set; } = "";
    public string Status { get; set; } = TaskItemStatus.Queued;
}

/// <summary>The task detail endpoint: the row, what it waits for and all its notes.</summary>
public sealed class TaskDetail
{
    public required TaskItem Task { get; init; }
    public IReadOnlyList<TaskDependencyInfo> Dependencies { get; init; } = [];
    public IReadOnlyList<HandoffNote> Notes { get; init; } = [];
}

/// <summary>A row of the decision list endpoint (Sql/Decision/List.sql): full text, no rationale.</summary>
public class DecisionListItem
{
    public long Id { get; set; }
    public string Title { get; set; } = "";
    public string Text { get; set; } = "";
    public string Status { get; set; } = DecisionStatus.Active;
    public long? SupersededById { get; set; }
    public long? TaskId { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>A task of the cross-project list (Sql/Task/ListAll.sql): a list row plus its project.</summary>
public sealed class TaskOverviewItem : TaskListItem
{
    public long ProjectId { get; set; }
    public string ProjectName { get; set; } = "";
    public bool ProjectIsActive { get; set; } = true;
}

/// <summary>A decision of the cross-project list (Sql/Decision/ListAll.sql): a list row plus its project.</summary>
public sealed class DecisionOverviewItem : DecisionListItem
{
    public long ProjectId { get; set; }
    public string ProjectName { get; set; } = "";
    public bool ProjectIsActive { get; set; } = true;
}
