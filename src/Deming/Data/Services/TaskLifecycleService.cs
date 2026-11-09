using System.Text.Json;
using Deming.Data.Models;
using Microsoft.Extensions.Options;

namespace Deming.Data.Services;

/// <summary>Input of <see cref="TaskLifecycleService.AddAsync"/>.</summary>
public sealed class AddTaskRequest
{
    public string Title { get; init; } = "";
    public string? Scope { get; init; }
    public int Priority { get; init; } = TaskPriority.Default;
    public string Source { get; init; } = TaskSource.Agent;

    /// <summary>Tasks this one waits for (ids in the same project).</summary>
    public IReadOnlyList<long> DependsOn { get; init; } = [];
}

/// <summary>
/// Input of <see cref="TaskLifecycleService.UpdateAsync"/>. A null field is left alone;
/// an empty <see cref="Scope"/> clears the scope; a non-null <see cref="DependsOn"/> replaces the whole set.
/// <see cref="Status"/> exists only so a request carrying one can be refused (400).
/// </summary>
public sealed class UpdateTaskRequest
{
    public string? Title { get; init; }
    public string? Scope { get; init; }
    public int? Priority { get; init; }
    public IReadOnlyList<long>? DependsOn { get; init; }
    public string? Status { get; init; }
}

/// <summary>Input of <see cref="TaskLifecycleService.DoneAsync"/> (scope § 5, handoff notes).</summary>
public sealed class DoneRequest
{
    public string Summary { get; init; } = "";
    public string? Caveats { get; init; }
    public string? TouchedFiles { get; init; }
    public string? CommitHash { get; init; }

    /// <summary>Close a task another session holds; the previous owner goes into the note.</summary>
    public bool Takeover { get; init; }
}

public sealed class DoneResult
{
    public required TaskItem Task { get; init; }
    public required HandoffNote Note { get; init; }

    /// <summary>Messages in the task's channel: information for the note, never a reason to refuse.</summary>
    public long MessageCount { get; init; }

    /// <summary>Estimated questions in the task's channel that got no reply (see Sql/Message/TaskStats.sql).</summary>
    public long UnansweredQuestionCount { get; init; }
}

/// <summary>
/// add, update, requeue, block, cancel, done. Same shape as <see cref="TaskService"/>:
/// every call is one BEGIN IMMEDIATE transaction, refusals are <see cref="TaskFlowException"/>.
/// Lock expiry is lazy and NOT applied here as a release: an expired lock is
/// simply treated as free by comparing <c>locked_at</c> with now − timeout.
/// </summary>
public sealed class TaskLifecycleService(DemingDb db, TimeProvider time, IOptions<TaskLockOptions> options)
{
    // ---------------------------------------------------------------- add

    /// <summary>
    /// Adds a queued task. A normalized title equal to an unfinished task's is refused
    /// (<see cref="TaskFlowError.DuplicateTitle"/>, with the existing id). The existing tasks are
    /// read in the same transaction; if that read fails the exception propagates and nothing is added.
    /// </summary>
    public Task<TaskItem> AddAsync(long projectId, string? sessionId, AddTaskRequest request, CancellationToken ct = default)
    {
        RequireText(request.Title, "Title");
        if (request.Priority is < TaskPriority.High or > TaskPriority.Low)
            throw Invalid("Priority must be 1 (high), 2 (normal) or 3 (low).");
        if (request.Source is not (TaskSource.Human or TaskSource.Agent))
            throw Invalid("Source must be 'human' or 'agent'.");

        return db.InTransactionAsync(async tx =>
        {
            if (await tx.Sql("Project.GetById").AddParameter("Id", projectId).GetModelAsync<Project>(ct) is null)
                throw new TaskFlowException(TaskFlowError.NotFound, $"Project {projectId} does not exist.");

            await ThrowIfDuplicateAsync(tx, projectId, request.Title, 0, ct);

            var dependsOn = await ValidatedDependenciesAsync(tx, projectId, 0, request.DependsOn, ct);

            var task = await tx.Sql("Task.Insert")
                .AddParameter("ProjectId", projectId)
                .AddParameter("Title", request.Title)
                .AddParameter("Scope", request.Scope)
                .AddParameter("Priority", request.Priority)
                .AddParameter("Source", request.Source)
                .AddParameter("AddedBySession", string.IsNullOrWhiteSpace(sessionId) ? null : sessionId)
                .GetModelAsync<TaskItem>(ct)
                ?? throw new InvalidOperationException("Task.Insert returned no row.");

            await InsertDependenciesAsync(tx, task.Id, dependsOn, ct);
            return task;
        }, ct);
    }

    // ------------------------------------------------------------- update

    /// <summary>
    /// Edits title, scope, priority and dependencies. Never changes status (a status in the request
    /// is a 400). An in-progress task is editable only by its live lock holder (an expired lock is
    /// free); done/cancelled by nobody. Atomic: one invalid dependency or a cycle writes no field.
    /// <paramref name="sessionId"/> may be empty for queued/blocked tasks (panel edits).
    /// </summary>
    public Task<TaskItem> UpdateAsync(long taskId, string? sessionId, UpdateTaskRequest request, CancellationToken ct = default)
    {
        if (request.Status is not null)
            throw new TaskFlowException(TaskFlowError.StatusNotUpdatable,
                "Update never changes status; use requeue, block, cancel or done.");
        if (request.Title is not null)
            RequireText(request.Title, "Title");
        if (request.Priority is < TaskPriority.High or > TaskPriority.Low)
            throw Invalid("Priority must be 1 (high), 2 (normal) or 3 (low).");

        return db.InTransactionAsync(async tx =>
        {
            var now = time.GetUtcNow().UtcDateTime;
            var task = await GetAsync(tx, taskId, ct);
            RequireNotFinished(task);
            if (task.Status == TaskItemStatus.InProgress)
                RequireLockUsable(task, sessionId, now, takeover: false);

            if (request.Title is not null)
                await ThrowIfDuplicateAsync(tx, task.ProjectId, request.Title, taskId, ct);

            IReadOnlyList<long>? dependsOn = null;
            if (request.DependsOn is not null)
                dependsOn = await ValidatedDependenciesAsync(tx, task.ProjectId, taskId, request.DependsOn, ct);

            var updated = await tx.Sql("Task.Update")
                .AddParameter("Id", taskId)
                .AddParameter("Title", request.Title)
                .AddParameter("Scope", request.Scope)
                .AddParameter("Priority", request.Priority)
                .GetModelAsync<TaskItem>(ct)
                ?? throw new InvalidOperationException("Task.Update returned no row.");

            if (dependsOn is not null)
            {
                await tx.Sql("Task.DeleteDependencies").AddParameter("Id", taskId).ExecuteAsync(ct);
                await InsertDependenciesAsync(tx, taskId, dependsOn, ct);
            }
            return updated;
        }, ct);
    }

    // ------------------------------------------------------------ requeue

    /// <summary>
    /// Puts a task back in the queue and clears its lock and blocked reason. Free: the session's own
    /// lock, an expired lock, a blocked task. Another session's live lock only with
    /// <paramref name="force"/>. Requeueing a queued, done or cancelled task is an error.
    /// </summary>
    public Task<TaskItem> RequeueAsync(long taskId, string sessionId, bool force = false, CancellationToken ct = default)
    {
        RequireSession(sessionId);
        return db.InTransactionAsync(async tx =>
        {
            var now = time.GetUtcNow().UtcDateTime;
            var task = await GetAsync(tx, taskId, ct);
            switch (task.Status)
            {
                case TaskItemStatus.Queued or TaskItemStatus.Done or TaskItemStatus.Cancelled:
                    throw new TaskFlowException(TaskFlowError.NotRequeueable,
                        $"Task {taskId} is {task.Status}; only in-progress and blocked tasks can be requeued.");
                case TaskItemStatus.InProgress:
                    RequireLockUsable(task, sessionId, now, takeover: force);
                    break;
            }
            return await tx.Sql("Task.Requeue").AddParameter("Id", taskId).GetModelAsync<TaskItem>(ct)
                ?? throw new InvalidOperationException("Task.Requeue returned no row.");
        }, ct);
    }

    // -------------------------------------------------------- block/cancel

    /// <summary>
    /// Temporary stop: reason required (also written as a note), lock released, task does not return
    /// to the queue. Allowed from queued and in-progress (own or expired lock).
    /// </summary>
    public Task<TaskItem> BlockAsync(long taskId, string sessionId, string reason, CancellationToken ct = default)
    {
        RequireSession(sessionId);
        RequireReason(reason);
        return db.InTransactionAsync(async tx =>
        {
            var now = time.GetUtcNow().UtcDateTime;
            var task = await GetAsync(tx, taskId, ct);
            RequireNotFinished(task);
            if (task.Status == TaskItemStatus.Blocked)
                throw new TaskFlowException(TaskFlowError.AlreadyBlocked, $"Task {taskId} is already blocked.");
            if (task.Status == TaskItemStatus.InProgress)
                RequireLockUsable(task, sessionId, now, takeover: false);

            var blocked = await tx.Sql("Task.Block")
                .AddParameter("Id", taskId).AddParameter("Reason", reason.Trim())
                .GetModelAsync<TaskItem>(ct)
                ?? throw new InvalidOperationException("Task.Block returned no row.");
            await InsertNoteAsync(tx, taskId, HandoffNoteKind.Note, reason.Trim(), null, null, null, sessionId, null, ct);
            return blocked;
        }, ct);
    }

    /// <summary>
    /// Permanent stop: reason required (also written as a note); the row stays and dependents treat
    /// it as done. Allowed from queued, blocked and in-progress (own or expired lock).
    /// </summary>
    public Task<TaskItem> CancelAsync(long taskId, string sessionId, string reason, CancellationToken ct = default)
    {
        RequireSession(sessionId);
        RequireReason(reason);
        return db.InTransactionAsync(async tx =>
        {
            var now = time.GetUtcNow().UtcDateTime;
            var task = await GetAsync(tx, taskId, ct);
            RequireNotFinished(task);
            if (task.Status == TaskItemStatus.InProgress)
                RequireLockUsable(task, sessionId, now, takeover: false);

            var cancelled = await tx.Sql("Task.Cancel")
                .AddParameter("Id", taskId).AddParameter("Reason", reason.Trim()).AddParameter("Now", now)
                .GetModelAsync<TaskItem>(ct)
                ?? throw new InvalidOperationException("Task.Cancel returned no row.");
            await InsertNoteAsync(tx, taskId, HandoffNoteKind.Note, reason.Trim(), null, null, null, sessionId, null, ct);
            return cancelled;
        }, ct);
    }

    // --------------------------------------------------------------- done

    /// <summary>
    /// Closes an in-progress task with a handoff note. Summary is required; caps are 800 / 800 / 1500
    /// characters and the API accepts 10 % more (<see cref="NoteLimits"/>). The lock holder closes
    /// freely; a live lock of another session needs <see cref="DoneRequest.Takeover"/>, and the previous
    /// owner is recorded in the note (also when the lock had expired). Choosing which notes
    /// <c>next</c> hands out is not done here.
    /// </summary>
    public Task<DoneResult> DoneAsync(long taskId, string sessionId, DoneRequest request, CancellationToken ct = default)
    {
        RequireSession(sessionId);
        RequireText(request.Summary, "Summary");
        RequireMax(request.Summary, NoteLimits.SummaryMax, "Summary");
        RequireMax(request.Caveats, NoteLimits.CaveatsMax, "Caveats");
        RequireMax(request.TouchedFiles, NoteLimits.TouchedFilesMax, "TouchedFiles");

        return db.InTransactionAsync(async tx =>
        {
            var now = time.GetUtcNow().UtcDateTime;
            var task = await GetAsync(tx, taskId, ct);
            RequireNotFinished(task);
            if (task.Status != TaskItemStatus.InProgress)
                throw new TaskFlowException(TaskFlowError.NotInProgress,
                    $"Task {taskId} is {task.Status}; take it before closing it.");

            RequireLockUsable(task, sessionId, now, request.Takeover);
            var previousOwner = task.LockOwner == sessionId ? null : task.LockOwner;

            var done = await tx.Sql("Task.Complete")
                .AddParameter("Id", taskId).AddParameter("Now", now)
                .GetModelAsync<TaskItem>(ct)
                ?? throw new InvalidOperationException("Task.Complete returned no row.");
            var note = await InsertNoteAsync(tx, taskId, HandoffNoteKind.Handoff, request.Summary.Trim(),
                request.Caveats, request.TouchedFiles, request.CommitHash, sessionId, previousOwner, ct);
            var messages = await MessageService.TaskStatsAsync(tx, taskId, ct);
            return new DoneResult
            {
                Task = done, Note = note,
                MessageCount = messages.MessageCount, UnansweredQuestionCount = messages.UnansweredQuestionCount,
            };
        }, ct);
    }

    // ------------------------------------------------------------ helpers

    private static async Task<TaskItem> GetAsync(DbScope tx, long taskId, CancellationToken ct) =>
        await tx.Sql("Task.GetById").AddParameter("Id", taskId).GetModelAsync<TaskItem>(ct)
        ?? throw new TaskFlowException(TaskFlowError.NotFound, $"Task {taskId} does not exist.");

    private static void RequireNotFinished(TaskItem task)
    {
        if (task.Status is TaskItemStatus.Done or TaskItemStatus.Cancelled)
            throw new TaskFlowException(TaskFlowError.AlreadyFinished, $"Task {task.Id} is {task.Status}.");
    }

    /// <summary>
    /// The task is in progress: passes when the session owns the lock, the lock has expired
    /// (locked_at before now − timeout, the same cut-off as Task.ReleaseExpiredLocks), or
    /// <paramref name="overrideLive"/> (force / takeover) is set.
    /// </summary>
    private void RequireLockUsable(TaskItem task, string? sessionId, DateTime now, bool takeover)
    {
        if (!string.IsNullOrEmpty(sessionId) && task.LockOwner == sessionId)
            return;
        if (task.LockedAt is { } lockedAt && lockedAt < now - options.Value.Timeout)
            return;
        if (takeover)
            return;
        throw new TaskFlowException(TaskFlowError.LockedByOtherSession,
            $"Task {task.Id} is locked by another session.");
    }

    private static async Task ThrowIfDuplicateAsync(DbScope tx, long projectId, string title, long excludeId, CancellationToken ct)
    {
        var existing = await tx.Sql("Task.FindOpenByTitle")
            .AddParameter("ProjectId", projectId).AddParameter("Title", title).AddParameter("ExcludeId", excludeId)
            .GetModelAsync<TaskItem>(ct);
        if (existing is not null)
            throw new TaskFlowException(TaskFlowError.DuplicateTitle,
                $"An unfinished task with this title already exists (id {existing.Id}).", existingTaskId: existing.Id);
    }

    /// <summary>Distinct dependency ids; each must exist in the project and differ from the task itself.</summary>
    private static async Task<IReadOnlyList<long>> ValidatedDependenciesAsync(
        DbScope tx, long projectId, long selfId, IReadOnlyList<long> ids, CancellationToken ct)
    {
        var distinct = ids.Distinct().ToList();
        if (distinct.Count == 0)
            return distinct;
        if (selfId != 0 && distinct.Contains(selfId))
            throw new TaskFlowException(TaskFlowError.InvalidDependency, "A task cannot depend on itself.");

        var found = await tx.Sql("Task.ExistingIds")
            .AddParameter("ProjectId", projectId).AddParameter("Ids", JsonSerializer.Serialize(distinct))
            .GetModelListAsync<TaskItem>(ct);
        var missing = distinct.Except(found.Select(t => t.Id)).ToList();
        if (missing.Count > 0)
            throw new TaskFlowException(TaskFlowError.InvalidDependency,
                $"Dependencies not found in this project: {string.Join(", ", missing)}.");
        return distinct;
    }

    private static async Task InsertDependenciesAsync(DbScope tx, long taskId, IReadOnlyList<long> ids, CancellationToken ct)
    {
        if (ids.Count == 0)
            return;
        try
        {
            await tx.Sql("Task.InsertDependencies")
                .AddParameter("Id", taskId).AddParameter("Ids", JsonSerializer.Serialize(ids))
                .ExecuteAsync(ct);
        }
        catch (ConstraintViolationException ex) when (ex.Is(ConstraintKind.Trigger, "dependency_cycle"))
        {
            throw new TaskFlowException(TaskFlowError.DependencyCycle,
                "These dependencies would make tasks wait for each other.");
        }
    }

    private static Task<HandoffNote> InsertNoteAsync(
        DbScope tx, long taskId, string kind, string summary, string? caveats, string? touchedFiles,
        string? commitHash, string? sessionId, string? takenOverFrom, CancellationToken ct) =>
        tx.Sql("HandoffNote.Insert")
            .AddParameter("TaskId", taskId).AddParameter("Kind", kind).AddParameter("Summary", summary)
            .AddParameter("Caveats", caveats).AddParameter("TouchedFiles", touchedFiles)
            .AddParameter("CommitHash", commitHash).AddParameter("SessionId", sessionId)
            .AddParameter("TakenOverFrom", takenOverFrom)
            .GetModelAsync<HandoffNote>(ct)!;

    private static void RequireSession(string? sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            throw new TaskFlowException(TaskFlowError.SessionRequired, "A session id is required.");
    }

    private static void RequireReason(string? reason)
    {
        RequireText(reason, "Reason");
        RequireMax(reason, NoteLimits.SummaryMax, "Reason");
    }

    private static void RequireText(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw Invalid($"{field} is required.");
    }

    private static void RequireMax(string? value, int max, string field)
    {
        if (value is not null && value.Length > max)
            throw Invalid($"{field} is {value.Length} characters; the limit is {max}.");
    }

    private static TaskFlowException Invalid(string message) => new(TaskFlowError.InvalidInput, message);
}
