using Deming.Data;
using Deming.Data.Models;
using Deming.Data.Services;
using Microsoft.Extensions.Options;

namespace Deming.Tests.Data;

/// <summary>add / update / requeue / block / cancel / done.</summary>
public class TaskLifecycleTests
{
    private sealed class MovableTime(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class Rig : IAsyncDisposable
    {
        public required TempDatabase Db { get; init; }
        public required MovableTime Time { get; init; }
        public required TaskService Flow { get; init; }
        public required TaskLifecycleService Life { get; init; }
        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<Rig> NewRigAsync()
    {
        var t = await TempDatabase.CreateAsync();
        await t.ExecAsync("INSERT INTO project (id, name, name_normalized) VALUES (1, 'Alpha', 'alpha'), (2, 'Beta', 'beta');");
        var time = new MovableTime(DateTimeOffset.UtcNow);
        var opts = Options.Create(new TaskLockOptions { TimeoutMinutes = 120 });
        return new Rig { Db = t, Time = time, Flow = new TaskService(t.Db, time, opts), Life = new TaskLifecycleService(t.Db, time, opts) };
    }

    private static Task<TaskItem> AddAsync(Rig r, string title, long project = 1, params long[] deps) =>
        r.Life.AddAsync(project, "s0", new AddTaskRequest { Title = title, DependsOn = deps }, Ct);

    private static async Task<TaskFlowException> RefusedAsync(Func<Task> act) =>
        await Assert.ThrowsAsync<TaskFlowException>(act);

    private static DoneRequest Done(string summary = "ok") => new() { Summary = summary };

    private static async Task<TaskItem> ReadAsync(Rig r, long id)
    {
        var v = await r.Db.Db.Sql("Task.GetById").AddParameter("Id", id).GetModelAsync<TaskItem>(Ct);
        return v!;
    }

    // --- add ---

    [Fact]
    public async Task Add_appends_a_queued_task_at_the_end_with_defaults()
    {
        await using var r = await NewRigAsync();
        var a = await AddAsync(r, "First");
        var b = await r.Life.AddAsync(1, "sess", new AddTaskRequest { Title = "  Second ", Scope = "  ", Priority = 1, Source = TaskSource.Human }, Ct);

        Assert.Equal(TaskItemStatus.Queued, a.Status);
        Assert.Equal(a.SortOrder + 10, b.SortOrder);
        Assert.Equal("Second", b.Title);
        Assert.Null(b.Scope);
        Assert.Equal(1, b.Priority);
        Assert.Equal("human", b.Source);
        Assert.Equal("sess", b.AddedBySession);
        Assert.Null(b.LockOwner);
    }

    [Fact]
    public async Task Without_a_source_the_session_id_decides_agent_or_human()
    {
        await using var r = await NewRigAsync();

        var typed = await r.Life.AddAsync(1, null, new AddTaskRequest { Title = "Typed in a terminal" }, Ct);
        var agent = await r.Life.AddAsync(1, "sess", new AddTaskRequest { Title = "Added by a session" }, Ct);
        var stated = await r.Life.AddAsync(1, null, new AddTaskRequest { Title = "Stated", Source = TaskSource.Agent }, Ct);

        Assert.Equal(TaskSource.Human, typed.Source);
        Assert.Equal(TaskSource.Agent, agent.Source);
        Assert.Equal(TaskSource.Agent, stated.Source);
    }

    [Fact]
    public async Task Add_rejects_a_title_matching_an_unfinished_task_and_returns_its_id()
    {
        await using var r = await NewRigAsync();
        var a = await AddAsync(r, "Fix the Login");
        var ex = await RefusedAsync(() => AddAsync(r, "  fix THE login "));
        Assert.Equal(TaskFlowError.DuplicateTitle, ex.Error);
        Assert.Equal(409, ex.HttpStatus);
        Assert.Equal(a.Id, ex.ExistingTaskId);

        // Same title in another project is fine.
        await AddAsync(r, "Fix the Login", project: 2);
    }

    [Fact]
    public async Task Add_allows_a_title_again_once_the_earlier_task_is_finished()
    {
        await using var r = await NewRigAsync();
        var a = await AddAsync(r, "Same");
        await r.Flow.TakeAsync(a.Id, "s1", Ct);
        await r.Life.DoneAsync(a.Id, "s1", Done(), Ct);
        var again = await AddAsync(r, "Same");
        Assert.NotEqual(a.Id, again.Id);
    }

    [Fact]
    public async Task Add_validates_input_and_project()
    {
        await using var r = await NewRigAsync();
        Assert.Equal(TaskFlowError.InvalidInput, (await RefusedAsync(() => AddAsync(r, "  "))).Error);
        Assert.Equal(TaskFlowError.InvalidInput,
            (await RefusedAsync(() => r.Life.AddAsync(1, "s", new AddTaskRequest { Title = "x", Priority = 4 }, Ct))).Error);
        Assert.Equal(TaskFlowError.InvalidInput,
            (await RefusedAsync(() => r.Life.AddAsync(1, "s", new AddTaskRequest { Title = "x", Source = "claude" }, Ct))).Error);
        Assert.Equal(TaskFlowError.NotFound, (await RefusedAsync(() => AddAsync(r, "x", project: 99))).Error);
    }

    [Fact]
    public async Task Add_with_an_unknown_or_foreign_dependency_adds_nothing()
    {
        await using var r = await NewRigAsync();
        var other = await AddAsync(r, "Other project", project: 2);

        Assert.Equal(TaskFlowError.InvalidDependency, (await RefusedAsync(() => AddAsync(r, "X", 1, 9999))).Error);
        Assert.Equal(TaskFlowError.InvalidDependency, (await RefusedAsync(() => AddAsync(r, "X", 1, other.Id))).Error);
        Assert.Equal(0L, Convert.ToInt64(await r.Db.ScalarAsync("SELECT count(*) FROM task WHERE project_id = 1")));
    }

    [Fact]
    public async Task Add_with_dependencies_keeps_the_task_out_of_next_until_they_finish()
    {
        await using var r = await NewRigAsync();
        var a = await AddAsync(r, "A");
        var b = await AddAsync(r, "B", 1, a.Id, a.Id);

        Assert.Equal(a.Id, (await r.Flow.NextAsync(1, "s1", Ct))!.Task.Id);
        Assert.Null(await r.Flow.NextAsync(1, "s2", Ct));
        await r.Life.DoneAsync(a.Id, "s1", Done(), Ct);
        Assert.Equal(b.Id, (await r.Flow.NextAsync(1, "s2", Ct))!.Task.Id);
    }

    // --- update ---

    [Fact]
    public async Task Update_edits_only_the_fields_sent()
    {
        await using var r = await NewRigAsync();
        var a = await r.Life.AddAsync(1, "s", new AddTaskRequest { Title = "Old", Scope = "scope", Priority = 3 }, Ct);

        var u = await r.Life.UpdateAsync(a.Id, null, new UpdateTaskRequest { Title = "New" }, Ct);
        Assert.Equal("New", u.Title);
        Assert.Equal("new", u.TitleNormalized);
        Assert.Equal("scope", u.Scope);
        Assert.Equal(3, u.Priority);

        u = await r.Life.UpdateAsync(a.Id, null, new UpdateTaskRequest { Scope = "", Priority = 1 }, Ct);
        Assert.Null(u.Scope);
        Assert.Equal(1, u.Priority);
        Assert.Equal("New", u.Title);
        Assert.Equal(TaskItemStatus.Queued, u.Status);
    }

    [Fact]
    public async Task Update_with_a_status_is_a_400_and_writes_nothing()
    {
        await using var r = await NewRigAsync();
        var a = await AddAsync(r, "A");
        var ex = await RefusedAsync(() => r.Life.UpdateAsync(a.Id, "s", new UpdateTaskRequest { Title = "Changed", Status = "done" }, Ct));
        Assert.Equal(TaskFlowError.StatusNotUpdatable, ex.Error);
        Assert.Equal(400, ex.HttpStatus);
        Assert.Equal("A", (await ReadAsync(r, a.Id)).Title);
    }

    [Fact]
    public async Task Update_of_an_in_progress_task_is_for_the_lock_holder_only()
    {
        await using var r = await NewRigAsync();
        var a = await AddAsync(r, "A");
        await r.Flow.TakeAsync(a.Id, "owner", Ct);

        var ex = await RefusedAsync(() => r.Life.UpdateAsync(a.Id, "intruder", new UpdateTaskRequest { Priority = 1 }, Ct));
        Assert.Equal(TaskFlowError.LockedByOtherSession, ex.Error);
        await RefusedAsync(() => r.Life.UpdateAsync(a.Id, null, new UpdateTaskRequest { Priority = 1 }, Ct));

        Assert.Equal(1, (await r.Life.UpdateAsync(a.Id, "owner", new UpdateTaskRequest { Priority = 1 }, Ct)).Priority);

        // The lock is not refreshed or moved by an edit; once it expires anyone may edit.
        r.Time.Advance(TimeSpan.FromMinutes(121));
        Assert.Equal(3, (await r.Life.UpdateAsync(a.Id, "intruder", new UpdateTaskRequest { Priority = 3 }, Ct)).Priority);
    }

    [Fact]
    public async Task Update_of_a_done_or_cancelled_task_is_refused_for_everyone()
    {
        await using var r = await NewRigAsync();
        var a = await AddAsync(r, "A");
        var b = await AddAsync(r, "B");
        await r.Flow.TakeAsync(a.Id, "s1", Ct);
        await r.Life.DoneAsync(a.Id, "s1", Done(), Ct);
        await r.Life.CancelAsync(b.Id, "s1", "not needed", Ct);

        foreach (var id in new[] { a.Id, b.Id })
        {
            var ex = await RefusedAsync(() => r.Life.UpdateAsync(id, "s1", new UpdateTaskRequest { Priority = 1 }, Ct));
            Assert.Equal(TaskFlowError.AlreadyFinished, ex.Error);
        }
    }

    [Fact]
    public async Task Update_with_one_invalid_dependency_writes_no_field()
    {
        await using var r = await NewRigAsync();
        var a = await AddAsync(r, "A");
        var b = await AddAsync(r, "B");
        var c = await AddAsync(r, "C", 1, a.Id);

        var ex = await RefusedAsync(() => r.Life.UpdateAsync(c.Id, "s",
            new UpdateTaskRequest { Title = "C2", Priority = 1, DependsOn = [b.Id, 9999] }, Ct));
        Assert.Equal(TaskFlowError.InvalidDependency, ex.Error);

        var after = await ReadAsync(r, c.Id);
        Assert.Equal("C", after.Title);
        Assert.Equal(2, after.Priority);
        Assert.Equal(a.Id, Convert.ToInt64(await r.Db.ScalarAsync($"SELECT depends_on_task_id FROM task_dependency WHERE task_id = {c.Id}")));
        Assert.Equal(1L, Convert.ToInt64(await r.Db.ScalarAsync($"SELECT count(*) FROM task_dependency WHERE task_id = {c.Id}")));
    }

    [Fact]
    public async Task Update_rejects_a_cycle_and_a_self_dependency_and_keeps_the_old_edges()
    {
        await using var r = await NewRigAsync();
        var a = await AddAsync(r, "A");
        var b = await AddAsync(r, "B", 1, a.Id);
        var c = await AddAsync(r, "C", 1, b.Id);

        var cycle = await RefusedAsync(() => r.Life.UpdateAsync(a.Id, "s",
            new UpdateTaskRequest { Priority = 1, DependsOn = [c.Id] }, Ct));
        Assert.Equal(TaskFlowError.DependencyCycle, cycle.Error);
        Assert.Equal(2, (await ReadAsync(r, a.Id)).Priority);

        var self = await RefusedAsync(() => r.Life.UpdateAsync(a.Id, "s", new UpdateTaskRequest { DependsOn = [a.Id] }, Ct));
        Assert.Equal(TaskFlowError.InvalidDependency, self.Error);

        Assert.Equal(2L, Convert.ToInt64(await r.Db.ScalarAsync("SELECT count(*) FROM task_dependency")));
    }

    [Fact]
    public async Task Update_replaces_the_dependency_set_and_empty_list_clears_it()
    {
        await using var r = await NewRigAsync();
        var a = await AddAsync(r, "A");
        var b = await AddAsync(r, "B");
        var c = await AddAsync(r, "C", 1, a.Id);

        await r.Life.UpdateAsync(c.Id, "s", new UpdateTaskRequest { DependsOn = [b.Id] }, Ct);
        Assert.Equal(b.Id, Convert.ToInt64(await r.Db.ScalarAsync($"SELECT depends_on_task_id FROM task_dependency WHERE task_id = {c.Id}")));

        await r.Life.UpdateAsync(c.Id, "s", new UpdateTaskRequest { DependsOn = [] }, Ct);
        Assert.Equal(0L, Convert.ToInt64(await r.Db.ScalarAsync("SELECT count(*) FROM task_dependency")));
    }

    [Fact]
    public async Task Update_cannot_rename_into_another_unfinished_title()
    {
        await using var r = await NewRigAsync();
        var a = await AddAsync(r, "A");
        var b = await AddAsync(r, "B");
        var ex = await RefusedAsync(() => r.Life.UpdateAsync(b.Id, "s", new UpdateTaskRequest { Title = "a" }, Ct));
        Assert.Equal(TaskFlowError.DuplicateTitle, ex.Error);
        Assert.Equal(a.Id, ex.ExistingTaskId);

        // Keeping its own title (different case) is fine.
        Assert.Equal("b", (await r.Life.UpdateAsync(b.Id, "s", new UpdateTaskRequest { Title = "b" }, Ct)).Title);
    }

    // --- requeue ---

    [Fact]
    public async Task Requeue_own_lock_returns_the_task_to_the_queue()
    {
        await using var r = await NewRigAsync();
        var a = await AddAsync(r, "A");
        await r.Flow.TakeAsync(a.Id, "s1", Ct);

        var q = await r.Life.RequeueAsync(a.Id, "s1", ct: Ct);
        Assert.Equal(TaskItemStatus.Queued, q.Status);
        Assert.Null(q.LockOwner);
        Assert.Null(q.LockedAt);
        Assert.NotNull(q.StartedAt);
        Assert.Equal(a.Id, (await r.Flow.NextAsync(1, "s2", Ct))!.Task.Id);
    }

    [Fact]
    public async Task Requeue_of_another_sessions_live_lock_needs_force()
    {
        await using var r = await NewRigAsync();
        var a = await AddAsync(r, "A");
        await r.Flow.TakeAsync(a.Id, "owner", Ct);

        var ex = await RefusedAsync(() => r.Life.RequeueAsync(a.Id, "other", ct: Ct));
        Assert.Equal(TaskFlowError.LockedByOtherSession, ex.Error);
        Assert.Equal("owner", (await ReadAsync(r, a.Id)).LockOwner);

        Assert.Equal(TaskItemStatus.Queued, (await r.Life.RequeueAsync(a.Id, "other", force: true, Ct)).Status);
    }

    [Fact]
    public async Task Requeue_of_an_expired_lock_is_free_even_though_the_task_still_shows_in_progress()
    {
        await using var r = await NewRigAsync();
        var a = await AddAsync(r, "A");
        await r.Flow.TakeAsync(a.Id, "owner", Ct);
        r.Time.Advance(TimeSpan.FromMinutes(121));

        Assert.Equal(TaskItemStatus.InProgress, (await ReadAsync(r, a.Id)).Status);
        Assert.Equal(TaskItemStatus.Queued, (await r.Life.RequeueAsync(a.Id, "other", ct: Ct)).Status);
    }

    [Fact]
    public async Task Requeue_of_a_blocked_task_is_free_and_clears_the_reason()
    {
        await using var r = await NewRigAsync();
        var a = await AddAsync(r, "A");
        await r.Flow.TakeAsync(a.Id, "s1", Ct);
        await r.Life.BlockAsync(a.Id, "s1", "waiting for key", Ct);

        var q = await r.Life.RequeueAsync(a.Id, "anyone", ct: Ct);
        Assert.Equal(TaskItemStatus.Queued, q.Status);
        Assert.Null(q.BlockedReason);
    }

    [Fact]
    public async Task Requeue_of_queued_done_or_cancelled_is_an_error_not_a_silent_success()
    {
        await using var r = await NewRigAsync();
        var queued = await AddAsync(r, "Q");
        var done = await AddAsync(r, "D");
        var cancelled = await AddAsync(r, "C");
        await r.Flow.TakeAsync(done.Id, "s1", Ct);
        await r.Life.DoneAsync(done.Id, "s1", Done(), Ct);
        await r.Life.CancelAsync(cancelled.Id, "s1", "no", Ct);

        foreach (var id in new[] { queued.Id, done.Id, cancelled.Id })
        {
            var ex = await RefusedAsync(() => r.Life.RequeueAsync(id, "s1", force: true, Ct));
            Assert.Equal(TaskFlowError.NotRequeueable, ex.Error);
            Assert.Equal(409, ex.HttpStatus);
        }
        Assert.Equal(TaskFlowError.NotFound, (await RefusedAsync(() => r.Life.RequeueAsync(999, "s1", ct: Ct))).Error);
        Assert.Equal(TaskFlowError.SessionRequired, (await RefusedAsync(() => r.Life.RequeueAsync(queued.Id, " ", ct: Ct))).Error);
    }

    [Fact]
    public async Task A_task_taken_from_blocked_loses_its_stale_reason_when_requeued()
    {
        await using var r = await NewRigAsync();
        var a = await AddAsync(r, "A");
        await r.Flow.TakeAsync(a.Id, "s1", Ct);
        await r.Life.BlockAsync(a.Id, "s1", "why", Ct);
        var taken = (await r.Flow.TakeAsync(a.Id, "s2", Ct)).Task;
        Assert.Equal("why", taken.BlockedReason); // take keeps it as history

        var q = await r.Life.RequeueAsync(a.Id, "s2", ct: Ct);
        Assert.Null(q.BlockedReason);
    }

    // --- block / cancel ---

    [Fact]
    public async Task Block_requires_a_reason_releases_the_lock_and_writes_a_note()
    {
        await using var r = await NewRigAsync();
        var a = await AddAsync(r, "A");
        await r.Flow.TakeAsync(a.Id, "s1", Ct);

        Assert.Equal(TaskFlowError.InvalidInput, (await RefusedAsync(() => r.Life.BlockAsync(a.Id, "s1", "  ", Ct))).Error);
        Assert.Equal(TaskItemStatus.InProgress, (await ReadAsync(r, a.Id)).Status);

        var b = await r.Life.BlockAsync(a.Id, "s1", " needs the API key ", Ct);
        Assert.Equal(TaskItemStatus.Blocked, b.Status);
        Assert.Equal("needs the API key", b.BlockedReason);
        Assert.Null(b.LockOwner);
        Assert.Equal("needs the API key", (string?)await r.Db.ScalarAsync($"SELECT summary FROM handoff_note WHERE task_id = {a.Id} AND kind = 'note'"));

        // Blocked tasks stay out of next.
        Assert.Null(await r.Flow.NextAsync(1, "s2", Ct));
        Assert.Equal(TaskFlowError.AlreadyBlocked, (await RefusedAsync(() => r.Life.BlockAsync(a.Id, "s1", "again", Ct))).Error);
    }

    [Fact]
    public async Task Block_by_another_session_while_the_lock_is_live_is_refused()
    {
        await using var r = await NewRigAsync();
        var a = await AddAsync(r, "A");
        await r.Flow.TakeAsync(a.Id, "owner", Ct);
        Assert.Equal(TaskFlowError.LockedByOtherSession, (await RefusedAsync(() => r.Life.BlockAsync(a.Id, "x", "why", Ct))).Error);
        Assert.Equal(TaskFlowError.LockedByOtherSession, (await RefusedAsync(() => r.Life.CancelAsync(a.Id, "x", "why", Ct))).Error);
    }

    [Fact]
    public async Task Cancel_is_permanent_keeps_the_row_and_releases_dependents()
    {
        await using var r = await NewRigAsync();
        var a = await AddAsync(r, "A");
        var b = await AddAsync(r, "B", 1, a.Id);

        Assert.Equal(TaskFlowError.InvalidInput, (await RefusedAsync(() => r.Life.CancelAsync(a.Id, "s1", "", Ct))).Error);

        var c = await r.Life.CancelAsync(a.Id, "s1", "superseded by B", Ct);
        Assert.Equal(TaskItemStatus.Cancelled, c.Status);
        Assert.Equal("superseded by B", c.CancelledReason);
        Assert.NotNull(c.FinishedAt);
        Assert.Equal(1L, Convert.ToInt64(await r.Db.ScalarAsync($"SELECT count(*) FROM handoff_note WHERE task_id = {a.Id}")));

        Assert.Equal(b.Id, (await r.Flow.NextAsync(1, "s2", Ct))!.Task.Id);
        Assert.Equal(TaskFlowError.AlreadyFinished, (await RefusedAsync(() => r.Life.CancelAsync(a.Id, "s1", "again", Ct))).Error);
        Assert.Equal(TaskFlowError.AlreadyFinished, (await RefusedAsync(() => r.Life.BlockAsync(a.Id, "s1", "again", Ct))).Error);
    }

    [Fact]
    public async Task Cancel_works_from_blocked()
    {
        await using var r = await NewRigAsync();
        var a = await AddAsync(r, "A");
        await r.Life.BlockAsync(a.Id, "s1", "stuck", Ct);
        var c = await r.Life.CancelAsync(a.Id, "s1", "gave up", Ct);
        Assert.Equal(TaskItemStatus.Cancelled, c.Status);
        Assert.Null(c.BlockedReason);
    }

    // --- done ---

    [Fact]
    public async Task Done_writes_the_handoff_note_and_releases_the_lock()
    {
        await using var r = await NewRigAsync();
        var a = await AddAsync(r, "A");
        await r.Flow.TakeAsync(a.Id, "s1", Ct);

        var res = await r.Life.DoneAsync(a.Id, "s1", new DoneRequest
        {
            Summary = " built it ", Caveats = "careful", TouchedFiles = "a.cs, b.cs", CommitHash = "abc1234",
        }, Ct);

        Assert.Equal(TaskItemStatus.Done, res.Task.Status);
        Assert.NotNull(res.Task.FinishedAt);
        Assert.Null(res.Task.LockOwner);
        Assert.Equal("built it", res.Note.Summary);
        Assert.Equal("careful", res.Note.Caveats);
        Assert.Equal("a.cs, b.cs", res.Note.TouchedFiles);
        Assert.Equal("abc1234", res.Note.CommitHash);
        Assert.Equal("s1", res.Note.SessionId);
        Assert.Equal(HandoffNoteKind.Handoff, res.Note.Kind);
        Assert.Null(res.Note.TakenOverFrom);
    }

    [Fact]
    public async Task Done_clears_a_stale_blocked_reason()
    {
        await using var r = await NewRigAsync();
        var a = await AddAsync(r, "A");
        await r.Flow.TakeAsync(a.Id, "s1", Ct);
        await r.Life.BlockAsync(a.Id, "s1", "why", Ct);
        await r.Flow.TakeAsync(a.Id, "s2", Ct);
        Assert.Null((await r.Life.DoneAsync(a.Id, "s2", Done(), Ct)).Task.BlockedReason);
    }

    [Fact]
    public async Task Done_without_a_summary_or_over_the_cap_is_rejected_and_the_task_stays_open()
    {
        await using var r = await NewRigAsync();
        var a = await AddAsync(r, "A");
        await r.Flow.TakeAsync(a.Id, "s1", Ct);

        Assert.Equal(TaskFlowError.InvalidInput, (await RefusedAsync(() => r.Life.DoneAsync(a.Id, "s1", Done("  "), Ct))).Error);
        Assert.Equal(TaskFlowError.InvalidInput, (await RefusedAsync(() => r.Life.DoneAsync(a.Id, "s1", Done(new string('x', 881)), Ct))).Error);
        Assert.Equal(TaskFlowError.InvalidInput, (await RefusedAsync(() => r.Life.DoneAsync(a.Id, "s1",
            new DoneRequest { Summary = "ok", Caveats = new string('x', 881) }, Ct))).Error);
        Assert.Equal(TaskFlowError.InvalidInput, (await RefusedAsync(() => r.Life.DoneAsync(a.Id, "s1",
            new DoneRequest { Summary = "ok", TouchedFiles = new string('x', 1651) }, Ct))).Error);
        Assert.Equal(TaskItemStatus.InProgress, (await ReadAsync(r, a.Id)).Status);
    }

    [Fact]
    public async Task Done_accepts_ten_percent_over_the_stated_cap()
    {
        await using var r = await NewRigAsync();
        var a = await AddAsync(r, "A");
        await r.Flow.TakeAsync(a.Id, "s1", Ct);
        var res = await r.Life.DoneAsync(a.Id, "s1", new DoneRequest
        {
            Summary = new string('s', 812), Caveats = new string('c', 880), TouchedFiles = new string('f', 1650),
        }, Ct);
        Assert.Equal(812, res.Note.Summary.Length);
        Assert.Equal(1650, res.Note.TouchedFiles!.Length);
    }

    [Fact]
    public async Task Done_by_another_session_needs_takeover_and_records_the_previous_owner()
    {
        await using var r = await NewRigAsync();
        var a = await AddAsync(r, "A");
        await r.Flow.TakeAsync(a.Id, "owner", Ct);

        var ex = await RefusedAsync(() => r.Life.DoneAsync(a.Id, "other", Done(), Ct));
        Assert.Equal(TaskFlowError.LockedByOtherSession, ex.Error);
        Assert.Equal(TaskItemStatus.InProgress, (await ReadAsync(r, a.Id)).Status);

        var res = await r.Life.DoneAsync(a.Id, "other", new DoneRequest { Summary = "finished for owner", Takeover = true }, Ct);
        Assert.Equal(TaskItemStatus.Done, res.Task.Status);
        Assert.Equal("owner", res.Note.TakenOverFrom);
        Assert.Equal("other", res.Note.SessionId);
    }

    [Fact]
    public async Task Done_from_a_state_other_than_in_progress_is_refused()
    {
        await using var r = await NewRigAsync();
        var queued = await AddAsync(r, "Q");
        var blocked = await AddAsync(r, "B");
        await r.Life.BlockAsync(blocked.Id, "s1", "why", Ct);

        Assert.Equal(TaskFlowError.NotInProgress, (await RefusedAsync(() => r.Life.DoneAsync(queued.Id, "s1", Done(), Ct))).Error);
        Assert.Equal(TaskFlowError.NotInProgress, (await RefusedAsync(() => r.Life.DoneAsync(blocked.Id, "s1", Done(), Ct))).Error);

        await r.Flow.TakeAsync(queued.Id, "s1", Ct);
        await r.Life.DoneAsync(queued.Id, "s1", Done(), Ct);
        Assert.Equal(TaskFlowError.AlreadyFinished, (await RefusedAsync(() => r.Life.DoneAsync(queued.Id, "s1", Done(), Ct))).Error);
        Assert.Equal(TaskFlowError.NotFound, (await RefusedAsync(() => r.Life.DoneAsync(999, "s1", Done(), Ct))).Error);
    }

    [Fact]
    public async Task Done_of_an_expired_lock_by_another_session_needs_no_takeover_but_names_the_old_owner()
    {
        await using var r = await NewRigAsync();
        var a = await AddAsync(r, "A");
        await r.Flow.TakeAsync(a.Id, "owner", Ct);
        r.Time.Advance(TimeSpan.FromMinutes(121));

        var res = await r.Life.DoneAsync(a.Id, "other", Done(), Ct);
        Assert.Equal("owner", res.Note.TakenOverFrom);
    }

    [Fact]
    public async Task Concurrent_done_closes_a_task_exactly_once()
    {
        await using var r = await NewRigAsync();
        var a = await AddAsync(r, "A");
        await r.Flow.TakeAsync(a.Id, "s1", Ct);

        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(async _ =>
        {
            try { await r.Life.DoneAsync(a.Id, "s1", Done(), Ct); return true; }
            catch (TaskFlowException ex) when (ex.Error == TaskFlowError.AlreadyFinished) { return false; }
        }));

        Assert.Equal(1, results.Count(x => x));
        Assert.Equal(1L, Convert.ToInt64(await r.Db.ScalarAsync($"SELECT count(*) FROM handoff_note WHERE task_id = {a.Id}")));
    }

    [Fact]
    public async Task Concurrent_add_of_the_same_title_creates_one_task()
    {
        await using var r = await NewRigAsync();
        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(async _ =>
        {
            try { await AddAsync(r, "Same title"); return true; }
            catch (TaskFlowException ex) when (ex.Error == TaskFlowError.DuplicateTitle) { return false; }
        }));

        Assert.Equal(1, results.Count(x => x));
        Assert.Equal(1L, Convert.ToInt64(await r.Db.ScalarAsync("SELECT count(*) FROM task")));
    }
}
