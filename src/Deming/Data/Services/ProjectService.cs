using Deming.Data.Models;

namespace Deming.Data.Services;

/// <summary>Fields a project update may change. Null = leave as is; an empty description or repo path clears it.</summary>
public sealed record ProjectChanges(string? Name = null, string? Description = null, string? RepoPath = null, bool? IsActive = null);

/// <summary>Projects. Every task, note and decision hangs off one. A project is never deleted, only made inactive.</summary>
public sealed class ProjectService(DemingDb db)
{
    /// <summary>Creates a project. A name that normalizes to an existing one is a
    /// <see cref="ConstraintViolationException"/> (Unique, ux_project_name / project.name_normalized).</summary>
    public async Task<Project> CreateAsync(string name, string? description = null, string? repoPath = null, CancellationToken ct = default) =>
        await db.Sql("Project.Insert")
            .AddParameter("Name", name)
            .AddParameter("Description", description)
            .AddParameter("RepoPath", repoPath)
            .GetModelAsync<Project>(ct)
        ?? throw new InvalidOperationException("Project.Insert returned no row.");

    /// <summary>Edits a project. A name that collides with another project is a unique violation, a blank one a check violation.</summary>
    public async Task<Project> UpdateAsync(long id, ProjectChanges changes, CancellationToken ct = default) =>
        await db.Sql("Project.Update")
            .AddParameter("Id", id)
            .AddParameter("Name", changes.Name)
            .AddParameter("Description", changes.Description)
            .AddParameter("RepoPath", changes.RepoPath)
            .AddParameter("IsActive", changes.IsActive)
            .GetModelAsync<Project>(ct)
        ?? throw new TaskFlowException(TaskFlowError.NotFound, $"Project {id} does not exist.");

    public Task<Project?> GetByNameAsync(string name, CancellationToken ct = default) =>
        db.Sql("Project.GetByName").AddParameter("Name", name).GetModelAsync<Project>(ct);

    public Task<Project?> GetByIdAsync(long id, CancellationToken ct = default) =>
        db.Sql("Project.GetById").AddParameter("Id", id).GetModelAsync<Project>(ct);

    /// <summary>All projects by name with their task counts (one query).</summary>
    public Task<List<ProjectSummary>> ListAsync(CancellationToken ct = default) =>
        db.Sql("Project.List").GetModelListAsync<ProjectSummary>(ct);
}
