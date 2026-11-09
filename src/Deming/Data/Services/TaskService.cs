using Deming.Data.Models;
using Microsoft.Extensions.Options;

namespace Deming.Data.Services;

/// <summary>Lock settings. Config key <c>Deming:Locks:TimeoutMinutes</c> (scope § 5: default 120).</summary>
public sealed class TaskLockOptions
{
    public const string SectionName = "Deming:Locks";

    /// <summary>Minutes without activity after which a lock is released lazily.</summary>
    public int TimeoutMinutes { get; set; } = 120;

    public TimeSpan Timeout => TimeSpan.FromMinutes(TimeoutMinutes);
}

/// <summary>Why a lock-flow call was refused. Maps to API errors later (HTTP in comments).</summary>
public enum TaskFlowError
{
    /// <summary>400: no session id.</summary>
    SessionRequired,
    /// <summary>404: no such project or task.</summary>
    NotFound,
    /// <summary>409: another session holds a live lock.</summary>
    LockedByOtherSession,
    /// <summary>409: the task is done or cancelled.</summary>
    AlreadyFinished,
    /// <summary>409: dependencies are not done/cancelled yet; see <see cref="TaskFlowException.PendingDependencies"/>.</summary>
    DependenciesPending,

    // --- lifecycle (TaskLifecycleService) ---

    /// <summary>400: empty title/reason/summary, bad priority or source, text over the cap.</summary>
    InvalidInput,
    /// <summary>400: a dependency id does not exist in the project (or is the task itself).</summary>
    InvalidDependency,
    /// <summary>400: update was sent a status; update never changes status.</summary>
    StatusNotUpdatable,
    /// <summary>409: an unfinished task with the same normalized title exists; see <see cref="TaskFlowException.ExistingTaskId"/>.</summary>
    DuplicateTitle,
    /// <summary>409: the new dependency would close a cycle.</summary>
    DependencyCycle,
    /// <summary>409: requeue of a task that is queued, done or cancelled.</summary>
    NotRequeueable,
    /// <summary>409: done of a task that is not in progress (take it first).</summary>
    NotInProgress,
    /// <summary>409: block of a task that is already blocked.</summary>
    AlreadyBlocked,

    // --- decisions (DecisionService) ---

    /// <summary>409: the decision was already superseded (one-way).</summary>
    AlreadySuperseded,

    // --- projects ---

    /// <summary>409: the project is inactive; next, take and add are refused. The database trigger
    /// is the last line; next also checks up front because an empty queue would otherwise answer 204.</summary>
    ProjectInactive,
}

public sealed class TaskFlowException(
    TaskFlowError error, string message, IReadOnlyList<TaskItem>? pending = null, long? existingTaskId = null)
    : Exception(message)
{
    public TaskFlowError Error { get; } = error;
    public IReadOnlyList<TaskItem> PendingDependencies { get; } = pending ?? [];

    /// <summary>For <see cref="TaskFlowError.DuplicateTitle"/>: the id of the task already there.</summary>
    public long? ExistingTaskId { get; } = existingTaskId;

    public int HttpStatus => Error switch
    {
        TaskFlowError.SessionRequired or TaskFlowError.InvalidInput or TaskFlowError.InvalidDependency
            or TaskFlowError.StatusNotUpdatable => 400,
        TaskFlowError.NotFound => 404,
        _ => 409,
    };
}

/// <summary>
/// What `next` / `take` hand back. <see cref="Notes"/>: notes of the dependencies first, then
/// the project's last 3 finished tasks (scope § 5). <see cref="Decisions"/>: the project's active
/// decisions, text cut at 250, no rationale. Both are read in the same transaction as the lock.
/// </summary>
public sealed class TaskLockResult
{
    public required TaskItem Task { get; init; }

    /// <summary>True when the session already held this task and only the lock was refreshed.</summary>
    public bool Resumed { get; init; }

    public IReadOnlyList<NextNote> Notes { get; init; } = [];
    public IReadOnlyList<NextDecision> Decisions { get; init; } = [];
}

/// <summary>Task queue and lock flow: next, take, lock timeout. Every call is one BEGIN IMMEDIATE transaction.</summary>
public sealed class TaskService(DemingDb db, TimeProvider time, IOptions<TaskLockOptions> options)
{
    /// <summary>
    /// Releases expired locks, then gives the session its held task (lock refreshed) or locks the
    /// first eligible one. Returns null when nothing is eligible (queue empty or all waiting).
    /// </summary>
    public Task<TaskLockResult?> NextAsync(long projectId, string sessionId, CancellationToken ct = default)
    {
        RequireSession(sessionId);
        return db.InTransactionAsync<TaskLockResult?>(async tx =>
        {
            var now = time.GetUtcNow().UtcDateTime;
            await ReleaseExpiredAsync(tx, now, ct);

            var project = await tx.Sql("Project.GetById").AddParameter("Id", projectId).GetModelAsync<Project>(ct)
                ?? throw new TaskFlowException(TaskFlowError.NotFound, $"Project {projectId} does not exist.");
            if (!project.IsActive)
                throw new TaskFlowException(TaskFlowError.ProjectInactive, $"Project {projectId} is inactive.");

            var held = await tx.Sql("Task.GetHeld")
                .AddParameter("ProjectId", projectId).AddParameter("SessionId", sessionId)
                .GetModelAsync<TaskItem>(ct);
            if (held is not null)
                return await RefreshAsync(tx, held, sessionId, now, ct);

            var locked = await tx.Sql("Task.LockNext")
                .AddParameter("ProjectId", projectId).AddParameter("SessionId", sessionId).AddParameter("Now", now)
                .GetModelAsync<TaskItem>(ct);
            return locked is null ? null : await WithContextAsync(tx, locked, false, ct);
        }, ct);
    }

    /// <summary>
    /// Takes a specific task, ignoring order and priority. Refused (<see cref="TaskFlowException"/>):
    /// another session's live lock, done/cancelled, unfinished dependencies. A blocked task can be
    /// taken. The session's other locks stay. Taking a task you already hold refreshes its lock.
    /// </summary>
    public Task<TaskLockResult> TakeAsync(long taskId, string sessionId, CancellationToken ct = default)
    {
        RequireSession(sessionId);
        return db.InTransactionAsync(async tx =>
        {
            var now = time.GetUtcNow().UtcDateTime;
            await ReleaseExpiredAsync(tx, now, ct);

            var locked = await tx.Sql("Task.LockById")
                .AddParameter("Id", taskId).AddParameter("SessionId", sessionId).AddParameter("Now", now)
                .GetModelAsync<TaskItem>(ct);
            if (locked is not null)
                return await WithContextAsync(tx, locked, false, ct);

            // Refused: find out why.
            var task = await tx.Sql("Task.GetById").AddParameter("Id", taskId).GetModelAsync<TaskItem>(ct)
                ?? throw new TaskFlowException(TaskFlowError.NotFound, $"Task {taskId} does not exist.");

            switch (task.Status)
            {
                case TaskItemStatus.InProgress when task.LockOwner == sessionId:
                    return await RefreshAsync(tx, task, sessionId, now, ct);
                case TaskItemStatus.InProgress:
                    throw new TaskFlowException(TaskFlowError.LockedByOtherSession,
                        $"Task {taskId} is locked by another session.");
                case TaskItemStatus.Done or TaskItemStatus.Cancelled:
                    throw new TaskFlowException(TaskFlowError.AlreadyFinished,
                        $"Task {taskId} is {task.Status}.");
                default:
                    var pending = await tx.Sql("Task.PendingDependencies").AddParameter("Id", taskId)
                        .GetModelListAsync<TaskItem>(ct);
                    throw new TaskFlowException(TaskFlowError.DependenciesPending,
                        $"Task {taskId} waits for {pending.Count} unfinished dependencies.", pending);
            }
        }, ct);
    }

    private Task<int> ReleaseExpiredAsync(DbScope tx, DateTime now, CancellationToken ct) =>
        tx.Sql("Task.ReleaseExpiredLocks").AddParameter("Cutoff", now - options.Value.Timeout).ExecuteAsync(ct);

    private static async Task<TaskLockResult> RefreshAsync(
        DbScope tx, TaskItem held, string sessionId, DateTime now, CancellationToken ct)
    {
        var refreshed = await tx.Sql("Task.RefreshLock")
            .AddParameter("Id", held.Id).AddParameter("SessionId", sessionId).AddParameter("Now", now)
            .GetModelAsync<TaskItem>(ct)
            ?? throw new InvalidOperationException("Lock vanished inside its own transaction.");
        return await WithContextAsync(tx, refreshed, true, ct);
    }

    /// <summary>Adds the notes and active decisions (same transaction as the lock).</summary>
    private static async Task<TaskLockResult> WithContextAsync(
        DbScope tx, TaskItem task, bool resumed, CancellationToken ct)
    {
        var notes = await tx.Sql("HandoffNote.ForNext")
            .AddParameter("TaskId", task.Id).AddParameter("ProjectId", task.ProjectId)
            .GetModelListAsync<NextNote>(ct);
        var decisions = await tx.Sql("Decision.ActiveForNext")
            .AddParameter("ProjectId", task.ProjectId).AddParameter("TextLimit", NextLimits.DecisionText)
            .GetModelListAsync<NextDecision>(ct);
        return new TaskLockResult { Task = task, Resumed = resumed, Notes = notes, Decisions = decisions };
    }

    private static void RequireSession(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            throw new TaskFlowException(TaskFlowError.SessionRequired, "A session id is required.");
    }
}
