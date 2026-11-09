using Deming.Data.Models;

namespace Deming.Data.Services;

/// <summary>Input of <see cref="FindingService.AddAsync"/>.</summary>
public sealed class FindingRequest
{
    public string Title { get; init; } = "";

    /// <summary>Markdown.</summary>
    public string Body { get; init; } = "";

    /// <summary>The task it was found in (a trace only; must belong to the same project).</summary>
    public long? TaskId { get; init; }

    /// <summary>The decision it relates to (a trace only; must belong to the same project).</summary>
    public long? DecisionId { get; init; }
}

/// <summary>
/// Findings of a project: list (no body), get (with body), add. Never deleted, never rewritten.
/// Field rules (blank, caps, same-project traces) live in the schema; a violated rule surfaces as
/// <see cref="ConstraintViolationException"/> for the API to translate.
/// </summary>
public sealed class FindingService(DemingDb db)
{
    public async Task<IReadOnlyList<FindingListItem>> ListAsync(long projectId, long? taskId, long? decisionId, CancellationToken ct = default) =>
        await db.InTransactionAsync<IReadOnlyList<FindingListItem>>(async tx =>
        {
            await RequireProjectAsync(tx, projectId, ct);
            return await tx.Sql("Finding.List")
                .AddParameter("ProjectId", projectId).AddParameter("TaskId", taskId).AddParameter("DecisionId", decisionId)
                .GetModelListAsync<FindingListItem>(ct);
        }, ct);

    /// <summary>
    /// Findings across projects, newest first, no body (the panel's Findings page). One project when
    /// <paramref name="projectId"/> is given (an unknown one is NotFound); otherwise every project,
    /// or only the active ones when <paramref name="activeOnly"/>.
    /// </summary>
    public async Task<IReadOnlyList<FindingOverviewItem>> ListAllAsync(long? projectId, bool activeOnly, CancellationToken ct = default) =>
        await db.InTransactionAsync<IReadOnlyList<FindingOverviewItem>>(async tx =>
        {
            if (projectId is { } id) await RequireProjectAsync(tx, id, ct);
            return await tx.Sql("Finding.ListAll")
                .AddParameter("ProjectId", projectId).AddParameter("ActiveOnly", activeOnly ? 1 : 0)
                .GetModelListAsync<FindingOverviewItem>(ct);
        }, ct);

    /// <summary>One finding with its body.</summary>
    public async Task<Finding> GetAsync(long id, CancellationToken ct = default) =>
        await db.Sql("Finding.GetById").AddParameter("Id", id).GetModelAsync<Finding>(ct)
        ?? throw new TaskFlowException(TaskFlowError.NotFound, $"Finding {id} does not exist.");

    public Task<Finding> AddAsync(long projectId, string? sessionId, FindingRequest request, CancellationToken ct = default) =>
        db.InTransactionAsync(async tx =>
        {
            await RequireProjectAsync(tx, projectId, ct);

            // Pre-checks for a readable message; the triggers stay the last line of defence.
            if (request.TaskId is { } taskId)
            {
                var task = await tx.Sql("Task.GetById").AddParameter("Id", taskId).GetModelAsync<TaskItem>(ct);
                if (task is null || task.ProjectId != projectId)
                    throw new TaskFlowException(TaskFlowError.InvalidInput, $"Task {taskId} does not exist in this project.");
            }
            if (request.DecisionId is { } decisionId)
            {
                var decision = await tx.Sql("Decision.GetById").AddParameter("Id", decisionId).GetModelAsync<Decision>(ct);
                if (decision is null || decision.ProjectId != projectId)
                    throw new TaskFlowException(TaskFlowError.InvalidInput, $"Decision {decisionId} does not exist in this project.");
            }

            return await tx.Sql("Finding.Insert")
                .AddParameter("ProjectId", projectId).AddParameter("TaskId", request.TaskId).AddParameter("DecisionId", request.DecisionId)
                .AddParameter("Title", request.Title ?? "").AddParameter("Body", request.Body ?? "")
                .AddParameter("SessionId", string.IsNullOrWhiteSpace(sessionId) ? null : sessionId)
                .GetModelAsync<Finding>(ct)
                ?? throw new InvalidOperationException("Finding.Insert returned no row.");
        }, ct);

    private static async Task RequireProjectAsync(DbScope tx, long projectId, CancellationToken ct)
    {
        if (await tx.Sql("Project.GetById").AddParameter("Id", projectId).GetModelAsync<Project>(ct) is null)
            throw new TaskFlowException(TaskFlowError.NotFound, $"Project {projectId} does not exist.");
    }
}
