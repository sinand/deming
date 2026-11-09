using Deming.Data;
using Deming.Data.Models;
using Deming.Data.Services;
using Microsoft.Extensions.Options;

namespace Deming.Tests.Data;

/// <summary>Notes and decisions returned by next / take.</summary>
public class NextContextTests
{
    private sealed class Rig : IAsyncDisposable
    {
        public required TempDatabase Db { get; init; }
        public required TaskService Flow { get; init; }
        public required TaskLifecycleService Life { get; init; }
        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<Rig> NewRigAsync()
    {
        var t = await TempDatabase.CreateAsync();
        await t.ExecAsync("INSERT INTO project (id, name, name_normalized) VALUES (1, 'Alpha', 'alpha'), (2, 'Beta', 'beta');");
        var opts = Options.Create(new TaskLockOptions());
        return new Rig
        {
            Db = t,
            Flow = new TaskService(t.Db, TimeProvider.System, opts),
            Life = new TaskLifecycleService(t.Db, TimeProvider.System, opts),
        };
    }

    private static async Task<long> FinishAsync(Rig r, string title, long project = 1, params long[] deps)
    {
        var t = await r.Life.AddAsync(project, "s0", new AddTaskRequest { Title = title, DependsOn = deps }, Ct);
        await r.Flow.TakeAsync(t.Id, "w", Ct);
        await r.Life.DoneAsync(t.Id, "w", new DoneRequest { Summary = "sum " + title, Caveats = "cav " + title }, Ct);
        await Task.Delay(5, Ct); // distinct finished_at
        return t.Id;
    }

    [Fact]
    public async Task Dependency_notes_come_first_then_the_last_three_finished_without_duplicates()
    {
        await using var r = await NewRigAsync();
        var old = await FinishAsync(r, "old");        // outside the last three
        var dep = await FinishAsync(r, "dep");        // dependency, also among the latest
        var f1 = await FinishAsync(r, "f1");
        var f2 = await FinishAsync(r, "f2");
        var f3 = await FinishAsync(r, "f3");
        await FinishAsync(r, "other-project", project: 2);
        var main = await r.Life.AddAsync(1, "s0", new AddTaskRequest { Title = "main", DependsOn = [dep, old] }, Ct);

        var res = await r.Flow.NextAsync(1, "s1", Ct);

        Assert.NotNull(res);
        Assert.Equal(main.Id, res!.Task.Id);
        // dependencies (by id) first, then the last 3 finished excluding dependencies, newest first
        Assert.Equal([old, dep, f3, f2, f1], res.Notes.Select(n => n.TaskId).ToArray());
        Assert.Equal(["dependency", "dependency", "recent", "recent", "recent"], res.Notes.Select(n => n.Source).ToArray());
        Assert.Equal("sum f3", res.Notes[2].Summary);
        Assert.Equal("cav f3", res.Notes[2].Caveats);
        Assert.Equal("f3", res.Notes[2].TaskTitle);
    }

    [Fact]
    public async Task Recent_covers_only_done_tasks_and_a_cancelled_dependency_brings_its_note()
    {
        await using var r = await NewRigAsync();
        var c = await r.Life.AddAsync(1, "s0", new AddTaskRequest { Title = "dropped" }, Ct);
        await r.Life.CancelAsync(c.Id, "s0", "not needed", Ct);
        var main = await r.Life.AddAsync(1, "s0", new AddTaskRequest { Title = "main", DependsOn = [c.Id] }, Ct);

        var res = await r.Flow.NextAsync(1, "s1", Ct);

        Assert.Equal(main.Id, res!.Task.Id);
        var note = Assert.Single(res.Notes);
        Assert.Equal("dependency", note.Source);
        Assert.Equal(HandoffNoteKind.Note, note.Kind);
    }

    [Fact]
    public async Task Take_and_resumed_next_return_notes_and_decisions_too()
    {
        await using var r = await NewRigAsync();
        var done = await FinishAsync(r, "done");
        await r.Db.ExecAsync("INSERT INTO decision (project_id, title, text) VALUES (1, 'D', 'keep it');");
        var t = await r.Life.AddAsync(1, "s0", new AddTaskRequest { Title = "t" }, Ct);

        var taken = await r.Flow.TakeAsync(t.Id, "s1", Ct);
        var again = await r.Flow.NextAsync(1, "s1", Ct);

        Assert.False(taken.Resumed);
        Assert.True(again!.Resumed);
        foreach (var res in new[] { taken, again })
        {
            Assert.Equal(done, Assert.Single(res.Notes).TaskId);
            Assert.Equal("keep it", Assert.Single(res.Decisions).Text);
        }
    }

    [Fact]
    public async Task Decisions_are_active_only_project_scoped_cut_at_250_without_rationale()
    {
        await using var r = await NewRigAsync();
        await r.Db.ExecAsync($"""
            INSERT INTO decision (id, project_id, title, text, rationale) VALUES
              (1, 1, 'Long', printf('%.300c', 'a'), 'secret why'),
              (2, 1, 'Exact', printf('%.250c', 'b'), NULL),
              (3, 1, 'Old', 'replaced', NULL),
              (4, 1, 'New', 'replacement', NULL),
              (5, 2, 'Other', 'other project', NULL);
            UPDATE decision SET status = 'superseded', superseded_by_id = 4, superseded_at = strftime('%Y-%m-%dT%H:%M:%fZ', 'now') WHERE id = 3;
            """);
        await r.Life.AddAsync(1, "s0", new AddTaskRequest { Title = "t" }, Ct);

        var res = await r.Flow.NextAsync(1, "s1", Ct);

        Assert.Equal([1L, 2L, 4L], res!.Decisions.Select(d => d.Id).ToArray());
        Assert.Equal(250, res.Decisions[0].Text.Length);
        Assert.True(res.Decisions[0].Truncated);
        Assert.Equal(250, res.Decisions[1].Text.Length);
        Assert.False(res.Decisions[1].Truncated);
        Assert.False(res.Decisions[2].Truncated);
        Assert.DoesNotContain(typeof(NextDecision).GetProperties(), p => p.Name == "Rationale");
    }
}
