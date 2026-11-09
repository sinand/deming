namespace Deming.Data.Models;

/// <summary>
/// Row of table <c>task</c>. Named TaskItem because "Task" collides with System.Threading.Tasks.Task.
/// </summary>
public sealed class TaskItem
{
    public long Id { get; set; }
    public long ProjectId { get; set; }
    public long SortOrder { get; set; }
    public string Title { get; set; } = "";
    public string TitleNormalized { get; set; } = "";
    public string? Scope { get; set; }
    public string Status { get; set; } = TaskItemStatus.Queued;
    public int Priority { get; set; } = TaskPriority.Default;
    public string Source { get; set; } = TaskSource.Agent;
    public string? AddedBySession { get; set; }
    public string? LockOwner { get; set; }
    public DateTime? LockedAt { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
    public string? BlockedReason { get; set; }
    public string? CancelledReason { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>Values of task.status (CHECK ck_task_status).</summary>
public static class TaskItemStatus
{
    public const string Queued = "queued";
    public const string InProgress = "in_progress";
    public const string Done = "done";
    public const string Blocked = "blocked";
    public const string Cancelled = "cancelled";

    public static readonly IReadOnlyList<string> All = [Queued, InProgress, Done, Blocked, Cancelled];
}

/// <summary>Values of task.source (CHECK ck_task_source): who added the task.</summary>
public static class TaskSource
{
    public const string Human = "human";
    public const string Agent = "agent";
}

/// <summary>task.priority (CHECK ck_task_priority): 1 high, 2 normal, 3 low; `next` takes lower first.</summary>
public static class TaskPriority
{
    public const int High = 1;
    public const int Default = 2;
    public const int Low = 3;
}

/// <summary>Row of table <c>task_dependency</c>: TaskId waits for DependsOnTaskId.</summary>
public sealed class TaskDependency
{
    public long TaskId { get; set; }
    public long DependsOnTaskId { get; set; }
    public DateTime CreatedAt { get; set; }
}
