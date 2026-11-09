using Deming.Data;
using Deming.Data.Models;
using Deming.Data.Services;
using Microsoft.Extensions.Options;

namespace Deming.Tests.Data;

/// <summary>Repo path, active flag and task counts of a project.</summary>
public class ProjectTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Rig(TempDatabase t) : IAsyncDisposable
    {
        public ProjectService Projects { get; } = new(t.Db);
        public TaskService Flow { get; } = new(t.Db, TimeProvider.System, Options.Create(new TaskLockOptions()));
        public TaskLifecycleService Life { get; } = new(t.Db, TimeProvider.System, Options.Create(new TaskLockOptions()));
        public ValueTask DisposeAsync() => t.DisposeAsync();
    }

    private static async Task<Rig> NewRigAsync() => new(await TempDatabase.CreateAsync());

    private static Task<TaskItem> AddAsync(Rig r, long project, string title) =>
        r.Life.AddAsync(project, "s0", new AddTaskRequest { Title = title }, Ct);

    [Fact]
    public async Task A_project_keeps_its_repo_path_and_starts_active()
    {
        await using var r = await NewRigAsync();

        var p = await r.Projects.CreateAsync("Alpha", repoPath: "  D:\\work\\alpha  ", ct: Ct);

        Assert.Equal("D:\\work\\alpha", p.RepoPath);
        Assert.True(p.IsActive);
        Assert.Null((await r.Projects.CreateAsync("Beta", ct: Ct)).RepoPath);
    }

    [Fact]
    public async Task Update_changes_only_what_is_given_and_empty_clears()
    {
        await using var r = await NewRigAsync();
        var p = await r.Projects.CreateAsync("Alpha", "first", "D:\\a", Ct);

        var renamed = await r.Projects.UpdateAsync(p.Id, new ProjectChanges(Name: "Köprü"), Ct);
        Assert.Equal("Köprü", renamed.Name);
        Assert.Equal("kopru", renamed.NameNormalized);
        Assert.Equal("first", renamed.Description);
        Assert.Equal("D:\\a", renamed.RepoPath);

        var cleared = await r.Projects.UpdateAsync(p.Id, new ProjectChanges(Description: "", RepoPath: "", IsActive: false), Ct);
        Assert.Null(cleared.Description);
        Assert.Null(cleared.RepoPath);
        Assert.False(cleared.IsActive);

        var missing = await Assert.ThrowsAsync<TaskFlowException>(() => r.Projects.UpdateAsync(999, new ProjectChanges(Name: "x"), Ct));
        Assert.Equal(TaskFlowError.NotFound, missing.Error);

        await r.Projects.CreateAsync("Beta", ct: Ct);
        var clash = await Assert.ThrowsAsync<ConstraintViolationException>(() => r.Projects.UpdateAsync(p.Id, new ProjectChanges(Name: "BETA"), Ct));
        Assert.Equal(ConstraintKind.Unique, clash.Kind);
    }

    [Fact]
    public async Task The_list_counts_tasks_per_project_in_one_query()
    {
        await using var r = await NewRigAsync();
        var a = await r.Projects.CreateAsync("Alpha", ct: Ct);
        var b = await r.Projects.CreateAsync("Beta", ct: Ct);
        await r.Projects.CreateAsync("Gamma", ct: Ct);

        var done = await AddAsync(r, a.Id, "one");
        await AddAsync(r, a.Id, "two");
        var dropped = await AddAsync(r, a.Id, "three");
        await AddAsync(r, b.Id, "other");
        await r.Flow.TakeAsync(done.Id, "s1", Ct);
        await r.Life.DoneAsync(done.Id, "s1", new DoneRequest { Summary = "ok" }, Ct);
        await r.Life.CancelAsync(dropped.Id, "s1", "not needed", Ct);

        var list = await r.Projects.ListAsync(Ct);

        Assert.Equal(["Alpha", "Beta", "Gamma"], list.Select(p => p.Name));
        // Cancelled tasks are not part of the total.
        Assert.Equal((2, 1, 1), (list[0].TotalTasks, list[0].DoneTasks, list[0].QueuedTasks));
        Assert.Equal((1, 0, 1), (list[1].TotalTasks, list[1].DoneTasks, list[1].QueuedTasks));
        Assert.Equal((0, 0, 0), (list[2].TotalTasks, list[2].DoneTasks, list[2].QueuedTasks));
    }

    [Fact]
    public async Task An_inactive_project_refuses_add_next_and_take_and_history_stays()
    {
        await using var r = await NewRigAsync();
        var p = await r.Projects.CreateAsync("Alpha", ct: Ct);
        var queued = await AddAsync(r, p.Id, "queued");
        var held = await AddAsync(r, p.Id, "held");
        await r.Flow.TakeAsync(held.Id, "s1", Ct);

        await r.Projects.UpdateAsync(p.Id, new ProjectChanges(IsActive: false), Ct);

        // add and take: the trigger (a database rule).
        var add = await Assert.ThrowsAsync<ConstraintViolationException>(() => AddAsync(r, p.Id, "new"));
        Assert.Equal((ConstraintKind.Trigger, "project_inactive"), (add.Kind, add.Constraint));
        var take = await Assert.ThrowsAsync<ConstraintViolationException>(() => r.Flow.TakeAsync(queued.Id, "s2", Ct));
        Assert.Equal("project_inactive", take.Constraint);
        // next: refused up front, also for a session that holds a task.
        var next = await Assert.ThrowsAsync<TaskFlowException>(() => r.Flow.NextAsync(p.Id, "s1", Ct));
        Assert.Equal(TaskFlowError.ProjectInactive, next.Error);
        Assert.Equal(409, next.HttpStatus);
        // Closing work that was already taken is not blocked.
        await r.Life.DoneAsync(held.Id, "s1", new DoneRequest { Summary = "ok" }, Ct);
        Assert.Equal(2, (await r.Projects.ListAsync(Ct)).Single().TotalTasks);

        await r.Projects.UpdateAsync(p.Id, new ProjectChanges(IsActive: true), Ct);
        Assert.NotNull(await r.Flow.NextAsync(p.Id, "s2", Ct));
        await AddAsync(r, p.Id, "new");
    }
}
