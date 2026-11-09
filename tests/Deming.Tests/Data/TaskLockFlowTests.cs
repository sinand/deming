using Deming.Data;
using Deming.Data.Models;
using Deming.Data.Services;
using Microsoft.Extensions.Options;

namespace Deming.Tests.Data;

/// <summary>next / take / lock timeout / concurrency.</summary>
public class TaskLockFlowTests
{
    private sealed class MovableTime(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static TaskService NewService(TempDatabase t, MovableTime? time = null, int timeoutMinutes = 120) =>
        new(t.Db, time ?? new MovableTime(DateTimeOffset.UtcNow),
            Options.Create(new TaskLockOptions { TimeoutMinutes = timeoutMinutes }));

    private static async Task<TempDatabase> SeedAsync(string tasksSql = "")
    {
        var t = await TempDatabase.CreateAsync();
        await t.ExecAsync("""
            INSERT INTO project (id, name, name_normalized) VALUES (1, 'Alpha', 'alpha'), (2, 'Beta', 'beta');
            """);
        if (tasksSql.Length > 0)
            await t.ExecAsync(tasksSql);
        return t;
    }

    private static string Row(long id, string status = "queued", int priority = 2, int order = 0, long project = 1) =>
        $"INSERT INTO task (id, project_id, sort_order, title, title_normalized, status, priority" +
        (status == "blocked" ? ", blocked_reason" : status == "cancelled" ? ", cancelled_reason, finished_at" : status == "done" ? ", finished_at" : "") +
        $") VALUES ({id}, {project}, {(order == 0 ? id * 10 : order)}, 'T{id}', 't{id}', '{status}', {priority}" +
        (status == "blocked" ? ", 'why'" : status == "cancelled" ? ", 'why', '2026-01-01T00:00:00.000Z'" : status == "done" ? ", '2026-01-01T00:00:00.000Z'" : "") + ");";

    private static string Dep(long task, long dependsOn) =>
        $"INSERT INTO task_dependency (task_id, depends_on_task_id) VALUES ({task}, {dependsOn});";

    // --- next ---

    [Fact]
    public async Task Next_requires_a_session_id()
    {
        await using var t = await SeedAsync(Row(1));
        var ex = await Assert.ThrowsAsync<TaskFlowException>(() => NewService(t).NextAsync(1, " ", Ct));
        Assert.Equal(TaskFlowError.SessionRequired, ex.Error);
    }

    [Fact]
    public async Task Next_locks_by_priority_then_order()
    {
        await using var t = await SeedAsync(Row(1, priority: 2, order: 10) + Row(2, priority: 1, order: 30) + Row(3, priority: 1, order: 20));
        var svc = NewService(t);

        var first = await svc.NextAsync(1, "s1", Ct);
        Assert.Equal(3, first!.Task.Id);
        Assert.False(first.Resumed);
        Assert.Equal("s1", first.Task.LockOwner);
        Assert.Equal(TaskItemStatus.InProgress, first.Task.Status);
        Assert.NotNull(first.Task.StartedAt);

        Assert.Equal(2, (await svc.NextAsync(1, "s2", Ct))!.Task.Id);
        Assert.Equal(1, (await svc.NextAsync(1, "s3", Ct))!.Task.Id);
        Assert.Null(await svc.NextAsync(1, "s4", Ct));
    }

    [Fact]
    public async Task Next_skips_blocked_and_finished_and_other_projects()
    {
        await using var t = await SeedAsync(Row(1, "blocked") + Row(2, "done") + Row(3, "cancelled") + Row(4, project: 2) + Row(5));
        Assert.Equal(5, (await NewService(t).NextAsync(1, "s1", Ct))!.Task.Id);
    }

    [Fact]
    public async Task Next_for_unknown_project_is_not_found()
    {
        await using var t = await SeedAsync();
        var ex = await Assert.ThrowsAsync<TaskFlowException>(() => NewService(t).NextAsync(99, "s1", Ct));
        Assert.Equal(TaskFlowError.NotFound, ex.Error);
    }

    [Fact]
    public async Task Same_session_gets_its_held_task_back_with_a_fresh_lock()
    {
        await using var t = await SeedAsync(Row(1) + Row(2));
        var time = new MovableTime(DateTimeOffset.UtcNow);
        var svc = NewService(t, time);

        var first = await svc.NextAsync(1, "s1", Ct);
        time.Advance(TimeSpan.FromMinutes(90));
        var again = await svc.NextAsync(1, "s1", Ct);

        Assert.Equal(first!.Task.Id, again!.Task.Id);
        Assert.True(again.Resumed);
        Assert.True(again.Task.LockedAt > first.Task.LockedAt);
        // The refresh means another 90 minutes later the lock (180 min after the first) still lives.
        time.Advance(TimeSpan.FromMinutes(90));
        Assert.Equal(2, (await svc.NextAsync(1, "s2", Ct))!.Task.Id);
    }

    [Fact]
    public async Task Next_waits_for_dependencies_and_done_or_cancelled_release_them()
    {
        await using var t = await SeedAsync(Row(1) + Row(2) + Row(3, "cancelled") + Row(4) + Dep(2, 1) + Dep(4, 3));
        var svc = NewService(t);

        // 1 is free, 2 waits for 1, 4 depends on a cancelled task and is free.
        Assert.Equal(1, (await svc.NextAsync(1, "s1", Ct))!.Task.Id);
        Assert.Equal(4, (await svc.NextAsync(1, "s2", Ct))!.Task.Id);
        Assert.Null(await svc.NextAsync(1, "s3", Ct));   // 2 still waits: 1 is in progress

        await t.ExecAsync("UPDATE task SET status = 'done', finished_at = '2026-01-01T00:00:00.000Z', lock_owner = NULL, locked_at = NULL WHERE id = 1;");
        Assert.Equal(2, (await svc.NextAsync(1, "s3", Ct))!.Task.Id);
    }

    // --- timeout ---

    [Fact]
    public async Task Expired_lock_is_released_lazily_by_next()
    {
        await using var t = await SeedAsync(Row(1));
        var time = new MovableTime(DateTimeOffset.UtcNow);
        var svc = NewService(t, time);

        await svc.NextAsync(1, "s1", Ct);
        time.Advance(TimeSpan.FromMinutes(119));
        Assert.Null(await svc.NextAsync(1, "s2", Ct));           // still live

        time.Advance(TimeSpan.FromMinutes(2));                   // 121 minutes
        var taken = await svc.NextAsync(1, "s2", Ct);
        Assert.Equal(1, taken!.Task.Id);
        Assert.Equal("s2", taken.Task.LockOwner);
        Assert.False(taken.Resumed);
    }

    [Fact]
    public async Task Timeout_is_configurable()
    {
        await using var t = await SeedAsync(Row(1));
        var time = new MovableTime(DateTimeOffset.UtcNow);
        var svc = NewService(t, time, timeoutMinutes: 10);

        await svc.NextAsync(1, "s1", Ct);
        time.Advance(TimeSpan.FromMinutes(11));
        Assert.Equal("s2", (await svc.NextAsync(1, "s2", Ct))!.Task.LockOwner);
    }

    [Fact]
    public async Task Previous_owner_loses_the_task_after_timeout_and_gets_a_new_one()
    {
        await using var t = await SeedAsync(Row(1) + Row(2));
        var time = new MovableTime(DateTimeOffset.UtcNow);
        var svc = NewService(t, time);

        await svc.NextAsync(1, "s1", Ct);                       // s1 holds 1
        time.Advance(TimeSpan.FromMinutes(121));
        Assert.Equal(1, (await svc.NextAsync(1, "s2", Ct))!.Task.Id);   // s2 inherits 1
        var s1Again = await svc.NextAsync(1, "s1", Ct);
        Assert.Equal(2, s1Again!.Task.Id);
        Assert.False(s1Again.Resumed);
    }

    // --- take ---

    [Fact]
    public async Task Take_ignores_order_and_priority()
    {
        await using var t = await SeedAsync(Row(1, priority: 1) + Row(2, priority: 3));
        var taken = await NewService(t).TakeAsync(2, "s1", Ct);
        Assert.Equal(2, taken.Task.Id);
        Assert.Equal("s1", taken.Task.LockOwner);
    }

    [Fact]
    public async Task Take_a_blocked_task_works()
    {
        await using var t = await SeedAsync(Row(1, "blocked"));
        var taken = await NewService(t).TakeAsync(1, "s1", Ct);
        Assert.Equal(TaskItemStatus.InProgress, taken.Task.Status);
    }

    [Fact]
    public async Task Take_refuses_another_sessions_live_lock()
    {
        await using var t = await SeedAsync(Row(1));
        var svc = NewService(t);
        await svc.TakeAsync(1, "s1", Ct);
        var ex = await Assert.ThrowsAsync<TaskFlowException>(() => svc.TakeAsync(1, "s2", Ct));
        Assert.Equal(TaskFlowError.LockedByOtherSession, ex.Error);
        Assert.Equal(409, ex.HttpStatus);
    }

    [Fact]
    public async Task Take_succeeds_over_an_expired_lock()
    {
        await using var t = await SeedAsync(Row(1));
        var time = new MovableTime(DateTimeOffset.UtcNow);
        var svc = NewService(t, time);
        await svc.TakeAsync(1, "s1", Ct);
        time.Advance(TimeSpan.FromMinutes(121));
        Assert.Equal("s2", (await svc.TakeAsync(1, "s2", Ct)).Task.LockOwner);
    }

    [Theory]
    [InlineData("done")]
    [InlineData("cancelled")]
    public async Task Take_refuses_finished_tasks(string status)
    {
        await using var t = await SeedAsync(Row(1, status));
        var ex = await Assert.ThrowsAsync<TaskFlowException>(() => NewService(t).TakeAsync(1, "s1", Ct));
        Assert.Equal(TaskFlowError.AlreadyFinished, ex.Error);
    }

    [Fact]
    public async Task Take_refuses_unfinished_dependencies_and_lists_them()
    {
        await using var t = await SeedAsync(Row(1) + Row(2, "blocked") + Row(3, "done") + Row(4) + Dep(4, 1) + Dep(4, 2) + Dep(4, 3));
        var ex = await Assert.ThrowsAsync<TaskFlowException>(() => NewService(t).TakeAsync(4, "s1", Ct));
        Assert.Equal(TaskFlowError.DependenciesPending, ex.Error);
        Assert.Equal([1L, 2L], ex.PendingDependencies.Select(d => d.Id).ToArray());
    }

    [Fact]
    public async Task Take_of_a_missing_task_is_not_found()
    {
        await using var t = await SeedAsync();
        var ex = await Assert.ThrowsAsync<TaskFlowException>(() => NewService(t).TakeAsync(42, "s1", Ct));
        Assert.Equal(TaskFlowError.NotFound, ex.Error);
        Assert.Equal(404, ex.HttpStatus);
    }

    [Fact]
    public async Task Take_keeps_the_sessions_other_locks_and_refreshes_a_held_one()
    {
        await using var t = await SeedAsync(Row(1) + Row(2));
        var svc = NewService(t);
        await svc.NextAsync(1, "s1", Ct);                 // holds 1
        await svc.TakeAsync(2, "s1", Ct);                 // also holds 2
        Assert.Equal(2L, await t.ScalarAsync("SELECT count(*) FROM task WHERE lock_owner = 's1';"));

        var again = await svc.TakeAsync(2, "s1", Ct);     // idempotent
        Assert.True(again.Resumed);
        // next gives the most recently locked task of the session.
        Assert.Equal(2, (await svc.NextAsync(1, "s1", Ct))!.Task.Id);
    }

    // --- concurrency ---

    [Fact]
    public async Task Two_parallel_sessions_cannot_take_the_same_task()
    {
        for (var round = 0; round < 5; round++)
        {
            await using var t = await SeedAsync(Row(1));
            var svc = NewService(t);
            var start = new TaskCompletionSource();

            async Task<string> Try(string session)
            {
                await start.Task;
                try { await svc.TakeAsync(1, session, Ct); return "won"; }
                catch (TaskFlowException ex) { return ex.Error.ToString(); }
            }

            var a = Try("sA");
            var b = Try("sB");
            start.SetResult();
            var results = (await Task.WhenAll(a, b)).Order().ToArray();

            Assert.Equal(["LockedByOtherSession", "won"], results);
            Assert.Equal(1L, await t.ScalarAsync("SELECT count(*) FROM task WHERE lock_owner IS NOT NULL;"));
        }
    }

    [Fact]
    public async Task Parallel_next_calls_hand_out_distinct_tasks()
    {
        const int tasks = 12, sessions = 20;
        var sql = string.Concat(Enumerable.Range(1, tasks).Select(i => Row(i)));
        await using var t = await SeedAsync(sql);
        var svc = NewService(t);
        var start = new TaskCompletionSource();

        var calls = Enumerable.Range(1, sessions).Select(async i =>
        {
            await start.Task;
            return await svc.NextAsync(1, $"s{i}", Ct);
        }).ToArray();
        start.SetResult();
        var results = await Task.WhenAll(calls);

        var ids = results.Where(r => r is not null).Select(r => r!.Task.Id).ToArray();
        Assert.Equal(tasks, ids.Length);
        Assert.Equal(tasks, ids.Distinct().Count());
        Assert.Equal(sessions - tasks, results.Count(r => r is null));
    }
}
