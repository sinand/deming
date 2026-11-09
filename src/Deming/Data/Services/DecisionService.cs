using Deming.Data.Models;

namespace Deming.Data.Services;

/// <summary>Input of <see cref="DecisionService.AddAsync"/> and <see cref="DecisionService.SupersedeAsync"/>.</summary>
public sealed class DecisionRequest
{
    public string Title { get; init; } = "";
    public string Text { get; init; } = "";
    public string? Rationale { get; init; }

    /// <summary>The task the decision was taken in (a trace only; must belong to the same project).</summary>
    public long? TaskId { get; init; }
}

public sealed class SupersedeResult
{
    public required Decision Superseded { get; init; }
    public required Decision Replacement { get; init; }
}

/// <summary>
/// Decisions of a project (scope § 5): list, get (with rationale), add, supersede. Never deleted.
/// Field rules (blank, text cap) live in the schema; a violated CHECK surfaces as
/// <see cref="ConstraintViolationException"/> for the API to translate.
/// </summary>
public sealed class DecisionService(DemingDb db, TimeProvider time)
{
    public async Task<IReadOnlyList<DecisionListItem>> ListAsync(long projectId, string? status, CancellationToken ct = default)
    {
        if (status is not (null or DecisionStatus.Active or DecisionStatus.Superseded))
            throw new TaskFlowException(TaskFlowError.InvalidInput,
                $"Unknown status '{status}'. Use '{DecisionStatus.Active}' or '{DecisionStatus.Superseded}'.");

        return await db.InTransactionAsync<IReadOnlyList<DecisionListItem>>(async tx =>
        {
            await RequireProjectAsync(tx, projectId, ct);
            return await tx.Sql("Decision.List")
                .AddParameter("ProjectId", projectId).AddParameter("Status", status)
                .GetModelListAsync<DecisionListItem>(ct);
        }, ct);
    }

    /// <summary>
    /// Decisions across projects, newest first (the panel's Decisions page). One project when
    /// <paramref name="projectId"/> is given (an unknown one is NotFound); otherwise every project,
    /// or only the active ones when <paramref name="activeOnly"/>.
    /// </summary>
    public async Task<IReadOnlyList<DecisionOverviewItem>> ListAllAsync(long? projectId, bool activeOnly, string? status, CancellationToken ct = default)
    {
        if (status is not (null or DecisionStatus.Active or DecisionStatus.Superseded))
            throw new TaskFlowException(TaskFlowError.InvalidInput,
                $"Unknown status '{status}'. Use '{DecisionStatus.Active}' or '{DecisionStatus.Superseded}'.");

        return await db.InTransactionAsync<IReadOnlyList<DecisionOverviewItem>>(async tx =>
        {
            if (projectId is { } id) await RequireProjectAsync(tx, id, ct);
            return await tx.Sql("Decision.ListAll")
                .AddParameter("ProjectId", projectId).AddParameter("ActiveOnly", activeOnly ? 1 : 0).AddParameter("Status", status)
                .GetModelListAsync<DecisionOverviewItem>(ct);
        }, ct);
    }

    /// <summary>One decision with its rationale.</summary>
    public async Task<Decision> GetAsync(long id, CancellationToken ct = default) =>
        await db.Sql("Decision.GetById").AddParameter("Id", id).GetModelAsync<Decision>(ct)
        ?? throw NotFound(id);

    public Task<Decision> AddAsync(long projectId, string? sessionId, DecisionRequest request, CancellationToken ct = default) =>
        db.InTransactionAsync(async tx =>
        {
            await RequireProjectAsync(tx, projectId, ct);
            return await InsertAsync(tx, projectId, sessionId, request, ct);
        }, ct);

    /// <summary>
    /// Records <paramref name="request"/> as a new decision and marks decision <paramref name="id"/>
    /// superseded by it, in one transaction. A decision that is already superseded raises the
    /// <c>decision_already_superseded</c> trigger; nothing is written then.
    /// </summary>
    public Task<SupersedeResult> SupersedeAsync(long id, string? sessionId, DecisionRequest request, CancellationToken ct = default) =>
        db.InTransactionAsync(async tx =>
        {
            var old = await tx.Sql("Decision.GetById").AddParameter("Id", id).GetModelAsync<Decision>(ct)
                ?? throw NotFound(id);
            var replacement = await InsertAsync(tx, old.ProjectId, sessionId, request, ct);
            var superseded = await tx.Sql("Decision.Supersede")
                .AddParameter("Id", id).AddParameter("ReplacementId", replacement.Id)
                .AddParameter("Now", time.GetUtcNow().UtcDateTime)
                .GetModelAsync<Decision>(ct)
                ?? throw new InvalidOperationException("Decision.Supersede returned no row.");
            return new SupersedeResult { Superseded = superseded, Replacement = replacement };
        }, ct);

    private static async Task<Decision> InsertAsync(
        DbScope tx, long projectId, string? sessionId, DecisionRequest request, CancellationToken ct)
    {
        // Pre-check for a readable message; the foreign key stays the last line of defence.
        if (request.TaskId is { } taskId)
        {
            var task = await tx.Sql("Task.GetById").AddParameter("Id", taskId).GetModelAsync<TaskItem>(ct);
            if (task is null || task.ProjectId != projectId)
                throw new TaskFlowException(TaskFlowError.InvalidInput, $"Task {taskId} does not exist in this project.");
        }

        return await tx.Sql("Decision.Insert")
            .AddParameter("ProjectId", projectId).AddParameter("TaskId", request.TaskId)
            .AddParameter("Title", request.Title ?? "").AddParameter("Text", request.Text ?? "")
            .AddParameter("Rationale", request.Rationale)
            .AddParameter("SessionId", string.IsNullOrWhiteSpace(sessionId) ? null : sessionId)
            .GetModelAsync<Decision>(ct)
            ?? throw new InvalidOperationException("Decision.Insert returned no row.");
    }

    private static async Task RequireProjectAsync(DbScope tx, long projectId, CancellationToken ct)
    {
        if (await tx.Sql("Project.GetById").AddParameter("Id", projectId).GetModelAsync<Project>(ct) is null)
            throw new TaskFlowException(TaskFlowError.NotFound, $"Project {projectId} does not exist.");
    }

    private static TaskFlowException NotFound(long id) =>
        new(TaskFlowError.NotFound, $"Decision {id} does not exist.");
}
