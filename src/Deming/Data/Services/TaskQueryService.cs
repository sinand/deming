using Deming.Data.Models;

namespace Deming.Data.Services;

/// <summary>Read side of tasks: the list (no scope) and one task with its dependencies and notes.</summary>
public sealed class TaskQueryService(DemingDb db)
{
    /// <summary>
    /// The project's tasks in queue order, optionally one status. An unknown status is
    /// <see cref="TaskFlowError.InvalidInput"/>, an unknown project <see cref="TaskFlowError.NotFound"/>.
    /// </summary>
    public async Task<IReadOnlyList<TaskListItem>> ListAsync(long projectId, string? status, CancellationToken ct = default)
    {
        if (status is not null && !TaskItemStatus.All.Contains(status))
            throw new TaskFlowException(TaskFlowError.InvalidInput,
                $"Unknown status '{status}'. Use one of: {string.Join(", ", TaskItemStatus.All)}.");

        return await ListAsync(projectId, status, eligibleOnly: false, ct);
    }

    /// <summary>
    /// The tasks next could take right now, in queue order ('deming parallel'): queued, every dependency
    /// done or cancelled, the project active. The rule lives in Sql/Task/List.sql, next to Task.LockNext.
    /// </summary>
    public Task<IReadOnlyList<TaskListItem>> EligibleAsync(long projectId, CancellationToken ct = default) =>
        ListAsync(projectId, null, eligibleOnly: true, ct);

    private Task<IReadOnlyList<TaskListItem>> ListAsync(long projectId, string? status, bool eligibleOnly, CancellationToken ct) =>
        db.InTransactionAsync<IReadOnlyList<TaskListItem>>(async tx =>
        {
            if (await tx.Sql("Project.GetById").AddParameter("Id", projectId).GetModelAsync<Project>(ct) is null)
                throw new TaskFlowException(TaskFlowError.NotFound, $"Project {projectId} does not exist.");
            return await tx.Sql("Task.List")
                .AddParameter("ProjectId", projectId).AddParameter("Status", status).AddParameter("EligibleOnly", eligibleOnly ? 1 : 0)
                .GetModelListAsync<TaskListItem>(ct);
        }, ct);

    /// <summary>
    /// Tasks across projects in queue order (the panel's Tasks page), one query. One project when
    /// <paramref name="projectId"/> is given (an unknown one is NotFound); otherwise every project,
    /// or only the active ones when <paramref name="activeOnly"/>. <paramref name="eligibleOnly"/> keeps only
    /// what next could take now (the panel's "Ready to take" filter).
    /// </summary>
    public async Task<IReadOnlyList<TaskOverviewItem>> ListAllAsync(long? projectId, bool activeOnly, string? status, bool eligibleOnly = false, CancellationToken ct = default)
    {
        if (status is not null && !TaskItemStatus.All.Contains(status))
            throw new TaskFlowException(TaskFlowError.InvalidInput,
                $"Unknown status '{status}'. Use one of: {string.Join(", ", TaskItemStatus.All)}.");

        return await db.InTransactionAsync<IReadOnlyList<TaskOverviewItem>>(async tx =>
        {
            if (projectId is { } id
                && await tx.Sql("Project.GetById").AddParameter("Id", id).GetModelAsync<Project>(ct) is null)
                throw new TaskFlowException(TaskFlowError.NotFound, $"Project {id} does not exist.");
            return await tx.Sql("Task.ListAll")
                .AddParameter("ProjectId", projectId).AddParameter("ActiveOnly", activeOnly ? 1 : 0).AddParameter("Status", status)
                .AddParameter("EligibleOnly", eligibleOnly ? 1 : 0)
                .GetModelListAsync<TaskOverviewItem>(ct);
        }, ct);
    }

    public Task<TaskDetail> GetAsync(long taskId, CancellationToken ct = default) =>
        db.InTransactionAsync(async tx =>
        {
            var task = await tx.Sql("Task.GetById").AddParameter("Id", taskId).GetModelAsync<TaskItem>(ct)
                ?? throw new TaskFlowException(TaskFlowError.NotFound, $"Task {taskId} does not exist.");
            var deps = await tx.Sql("Task.Dependencies").AddParameter("Id", taskId).GetModelListAsync<TaskDependencyInfo>(ct);
            var notes = await tx.Sql("HandoffNote.ByTask").AddParameter("TaskId", taskId).GetModelListAsync<HandoffNote>(ct);
            return new TaskDetail { Task = task, Dependencies = deps, Notes = notes };
        }, ct);
}
