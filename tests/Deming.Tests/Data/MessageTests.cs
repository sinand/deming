using System.Collections.Concurrent;
using System.Diagnostics;
using Deming.Data;
using Deming.Data.Models;
using Deming.Data.Services;
using Microsoft.Extensions.Options;

namespace Deming.Tests.Data;

/// <summary>The message channel: schema rules, cursor reads, waiting, parallel writers, done counts.</summary>
public class MessageTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Rig : IAsyncDisposable
    {
        public required TempDatabase Db { get; init; }
        public required MessageService Messages { get; init; }
        public required TaskService Flow { get; init; }
        public required TaskLifecycleService Life { get; init; }
        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

    private static async Task<Rig> NewRigAsync()
    {
        var t = await TempDatabase.CreateAsync();
        await t.ExecAsync("INSERT INTO project (id, name, name_normalized) VALUES (1, 'Alpha', 'alpha'), (2, 'Beta', 'beta');");
        var opts = Options.Create(new TaskLockOptions { TimeoutMinutes = 120 });
        return new Rig
        {
            Db = t,
            Messages = new MessageService(t.Db, TimeProvider.System),
            Flow = new TaskService(t.Db, TimeProvider.System, opts),
            Life = new TaskLifecycleService(t.Db, TimeProvider.System, opts),
        };
    }

    private static Task<MessagePosted> PostAsync(Rig r, string body, string sender = "claude", long project = 1,
        long? task = null, string? to = null, long? reply = null) =>
        r.Messages.PostAsync("s1", new MessageRequest { Project = project, TaskId = task, Sender = sender, To = to, ReplyToId = reply, Body = body }, Ct);

    private static async Task<ConstraintViolationException> ViolatesAsync(Func<Task> act) =>
        await Assert.ThrowsAsync<ConstraintViolationException>(act);

    private static async Task<long> TaskAsync(Rig r, string title, long project = 1) =>
        (await r.Life.AddAsync(project, "s0", new AddTaskRequest { Title = title }, Ct)).Id;

    [Fact]
    public async Task Labels_are_lowercased_and_anything_but_lowercase_ascii_is_refused()
    {
        await using var r = await NewRigAsync();

        var posted = await PostAsync(r, "hello", sender: " Codex ", to: "CLAUDE");
        Assert.Equal("codex", posted.Sender);
        Assert.Equal("claude", posted.Recipient);
        Assert.Equal("s1", posted.SenderSessionId);

        Assert.Null((await PostAsync(r, "to all", to: "  ")).Recipient);
        Assert.Equal("worker-2.a_b", (await PostAsync(r, "x", sender: "Worker-2.A_B")).Sender);

        Assert.True((await ViolatesAsync(() => PostAsync(r, "x", sender: "çalışan"))).Is(ConstraintKind.Check, "ck_message_sender"));
        Assert.True((await ViolatesAsync(() => PostAsync(r, "x", sender: "two words"))).Is(ConstraintKind.Check, "ck_message_sender"));
        Assert.True((await ViolatesAsync(() => PostAsync(r, "x", sender: ""))).Is(ConstraintKind.Check, "ck_message_sender"));
        Assert.True((await ViolatesAsync(() => PostAsync(r, "x", sender: new string('a', 65)))).Is(ConstraintKind.Check, "ck_message_sender"));
        Assert.True((await ViolatesAsync(() => PostAsync(r, "x", to: "öteki"))).Is(ConstraintKind.Check, "ck_message_recipient"));
    }

    [Fact]
    public async Task Body_is_required_and_capped_with_ten_percent_tolerance()
    {
        await using var r = await NewRigAsync();

        await PostAsync(r, new string('x', MessageLimits.BodyMax));
        Assert.True((await ViolatesAsync(() => PostAsync(r, new string('x', MessageLimits.BodyMax + 1)))).Is(ConstraintKind.Check, "ck_message_body"));
        Assert.True((await ViolatesAsync(() => PostAsync(r, "   "))).Is(ConstraintKind.Check, "ck_message_body"));
    }

    [Fact]
    public async Task Task_channel_must_be_in_the_project_and_is_read_only_once_finished()
    {
        await using var r = await NewRigAsync();
        var a = await TaskAsync(r, "A");
        var other = await TaskAsync(r, "B", project: 2);

        Assert.Equal(a, (await PostAsync(r, "in a", task: a)).TaskId);
        Assert.True((await ViolatesAsync(() => PostAsync(r, "x", task: other))).Is(ConstraintKind.Trigger, "message_task_other_project"));
        Assert.True((await ViolatesAsync(() => PostAsync(r, "x", task: 999))).Is(ConstraintKind.Trigger, "message_task_other_project"));

        await r.Flow.TakeAsync(a, "s1", Ct);
        await r.Life.DoneAsync(a, "s1", new DoneRequest { Summary = "ok" }, Ct);
        Assert.True((await ViolatesAsync(() => PostAsync(r, "late", task: a))).Is(ConstraintKind.Trigger, "message_task_finished"));

        var b = await TaskAsync(r, "C");
        await r.Life.CancelAsync(b, "s1", "not needed", Ct);
        Assert.True((await ViolatesAsync(() => PostAsync(r, "late", task: b))).Is(ConstraintKind.Trigger, "message_task_finished"));

        // Kept, still readable.
        var page = await r.Messages.ReadAsync(new MessageQuery(1, TaskId: a), 0, Ct);
        Assert.Equal("in a", Assert.Single(page.Messages).Body);
    }

    [Fact]
    public async Task A_reply_stays_in_its_channel()
    {
        await using var r = await NewRigAsync();
        var a = await TaskAsync(r, "A");
        var b = await TaskAsync(r, "B");
        var question = await PostAsync(r, "which file?", task: a);

        Assert.Equal(question.Id, (await PostAsync(r, "this one", task: a, reply: question.Id)).ReplyToId);
        Assert.True((await ViolatesAsync(() => PostAsync(r, "x", task: b, reply: question.Id))).Is(ConstraintKind.Trigger, "message_reply_other_channel"));
        Assert.True((await ViolatesAsync(() => PostAsync(r, "x", reply: question.Id))).Is(ConstraintKind.Trigger, "message_reply_other_channel"));
        Assert.True((await ViolatesAsync(() => PostAsync(r, "x", project: 2, reply: question.Id))).Is(ConstraintKind.Trigger, "message_reply_other_channel"));
        Assert.True((await ViolatesAsync(() => PostAsync(r, "x", task: a, reply: 999))).Is(ConstraintKind.Trigger, "message_reply_other_channel"));
    }

    [Fact]
    public async Task Messages_are_never_rewritten_and_go_only_with_their_task_and_ids_are_never_reused()
    {
        await using var r = await NewRigAsync();
        var a = await TaskAsync(r, "A");
        var kept = await PostAsync(r, "project channel");
        var first = await PostAsync(r, "q", task: a);
        var last = await PostAsync(r, "answer", task: a, reply: first.Id);

        Assert.True((await ViolatesAsync(() => r.Db.ExecAsync($"UPDATE message SET body = 'changed' WHERE id = {kept.Id};")))
            .Is(ConstraintKind.Trigger, "message_immutable"));

        // No code path deletes a task; the cascade is what a raw delete does.
        await r.Db.ExecAsync($"DELETE FROM task WHERE id = {a};");
        Assert.Equal(1L, await r.Db.ScalarAsync("SELECT count(*) FROM message;"));

        var next = await PostAsync(r, "after the delete");
        Assert.True(next.Id > last.Id, "a deleted id must not be handed out again (cursors already passed it)");
    }

    [Fact]
    public async Task Reads_follow_the_cursor_channel_and_recipient()
    {
        await using var r = await NewRigAsync();
        var a = await TaskAsync(r, "A");
        var m1 = await PostAsync(r, "all from claude", sender: "claude");
        var m2 = await PostAsync(r, "to codex", sender: "claude", to: "codex");
        var m3 = await PostAsync(r, "to claude", sender: "codex", to: "claude");
        var m4 = await PostAsync(r, "all from codex", sender: "codex");
        var m5 = await PostAsync(r, "in task", sender: "codex", task: a);
        await PostAsync(r, "other project", project: 2);

        var project = await r.Messages.ReadAsync(new MessageQuery(1), 0, Ct);
        Assert.Equal([m1.Id, m2.Id, m3.Id, m4.Id], project.Messages.Select(m => m.Id));
        Assert.Equal(m4.Id, project.LastMessageId);
        Assert.False(project.HasMore);

        var task = await r.Messages.ReadAsync(new MessageQuery(1, TaskId: a), 0, Ct);
        Assert.Equal(m5.Id, Assert.Single(task.Messages).Id);

        var all = await r.Messages.ReadAsync(new MessageQuery(1, All: true), 0, Ct);
        Assert.Equal([m1.Id, m2.Id, m3.Id, m4.Id, m5.Id], all.Messages.Select(m => m.Id));

        // to=claude: addressed to claude, plus to everyone, minus claude's own "to everyone". Case does not matter.
        var forClaude = await r.Messages.ReadAsync(new MessageQuery(1, To: "Claude"), 0, Ct);
        Assert.Equal([m3.Id, m4.Id], forClaude.Messages.Select(m => m.Id));

        var afterCursor = await r.Messages.ReadAsync(new MessageQuery(1, After: m2.Id), 0, Ct);
        Assert.Equal([m3.Id, m4.Id], afterCursor.Messages.Select(m => m.Id));

        var paged = await r.Messages.ReadAsync(new MessageQuery(1, Top: 3), 0, Ct);
        Assert.Equal(3, paged.Messages.Count);
        Assert.True(paged.HasMore);
        Assert.Equal(m3.Id, paged.LastMessageId);

        var empty = await r.Messages.ReadAsync(new MessageQuery(1, After: m4.Id), 0, Ct);
        Assert.Empty(empty.Messages);
        Assert.Equal(m4.Id, empty.LastMessageId);
    }

    [Fact]
    public async Task Read_refusals()
    {
        await using var r = await NewRigAsync();
        var other = await TaskAsync(r, "B", project: 2);

        async Task<TaskFlowError> Refused(MessageQuery q, int wait = 0) =>
            (await Assert.ThrowsAsync<TaskFlowException>(() => r.Messages.ReadAsync(q, wait, Ct))).Error;

        Assert.Equal(TaskFlowError.InvalidInput, await Refused(new MessageQuery(1), MessageLimits.WaitMaxSeconds + 1));
        Assert.Equal(TaskFlowError.InvalidInput, await Refused(new MessageQuery(1), -1));
        Assert.Equal(TaskFlowError.InvalidInput, await Refused(new MessageQuery(1, Top: 0)));
        Assert.Equal(TaskFlowError.InvalidInput, await Refused(new MessageQuery(1, Top: MessageLimits.TopMax + 1)));
        Assert.Equal(TaskFlowError.InvalidInput, await Refused(new MessageQuery(1, TaskId: other, All: true)));
        Assert.Equal(TaskFlowError.NotFound, await Refused(new MessageQuery(99)));
        Assert.Equal(TaskFlowError.NotFound, await Refused(new MessageQuery(1, TaskId: other)));
        Assert.Equal(TaskFlowError.NotFound,
            (await Assert.ThrowsAsync<TaskFlowException>(() => r.Messages.PostAsync(null, new MessageRequest { Project = 99, Sender = "a", Body = "x" }, Ct))).Error);
        Assert.Equal(TaskFlowError.NotFound, (await Assert.ThrowsAsync<TaskFlowException>(() => r.Messages.GetAsync(12345, Ct))).Error);
    }

    [Fact]
    public async Task A_waiting_read_returns_when_a_message_is_written_and_empty_when_time_runs_out()
    {
        await using var r = await NewRigAsync();

        var clock = Stopwatch.StartNew();
        var empty = await r.Messages.ReadAsync(new MessageQuery(1), 1, Ct);
        Assert.Empty(empty.Messages);
        Assert.True(clock.Elapsed >= TimeSpan.FromSeconds(0.9), $"returned after {clock.Elapsed}");

        clock.Restart();
        var waiting = r.Messages.ReadAsync(new MessageQuery(1, To: "codex"), 30, Ct);
        await Task.Delay(200, Ct);
        Assert.False(waiting.IsCompleted);
        await PostAsync(r, "own message to everyone does not wake me", sender: "codex");
        await Task.Delay(200, Ct);
        Assert.False(waiting.IsCompleted);
        var sent = await PostAsync(r, "wake up", sender: "claude", to: "codex");

        var page = await waiting;
        Assert.Equal(sent.Id, Assert.Single(page.Messages).Id);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"took {clock.Elapsed}");
    }

    [Fact]
    public async Task Parallel_writers_and_a_cursor_reader_miss_nothing_and_see_nothing_twice()
    {
        await using var r = await NewRigAsync();
        const int writers = 8, perWriter = 15;

        var seen = new ConcurrentBag<long>();
        using var stop = new CancellationTokenSource();
        var reader = Task.Run(async () =>
        {
            long cursor = 0;
            while (seen.Count < writers * perWriter)
            {
                var page = await r.Messages.ReadAsync(new MessageQuery(1, After: cursor, Top: 7), 1, Ct);
                foreach (var m in page.Messages)
                    seen.Add(m.Id);
                cursor = page.LastMessageId;
            }
        }, Ct);

        // Separate service instances: each writer stands for another server request, not a shared signal.
        var written = await Task.WhenAll(Enumerable.Range(0, writers).Select(w => Task.Run(async () =>
        {
            var service = new MessageService(r.Db.Db, TimeProvider.System);
            var ids = new List<long>();
            for (var i = 0; i < perWriter; i++)
                ids.Add((await service.PostAsync(null, new MessageRequest { Project = 1, Sender = $"w{w}", Body = $"{w}:{i}" }, Ct)).Id);
            return ids;
        }, Ct)));

        await reader.WaitAsync(TimeSpan.FromSeconds(60), Ct);
        var all = written.SelectMany(x => x).Order().ToList();
        Assert.Equal(writers * perWriter, all.Distinct().Count());
        Assert.Equal(all, seen.Order().ToList());
    }

    [Fact]
    public async Task Done_reports_message_and_unanswered_question_counts_without_refusing()
    {
        await using var r = await NewRigAsync();
        var a = await TaskAsync(r, "A");
        var answered = await PostAsync(r, "which branch?", task: a);
        await PostAsync(r, "main", task: a, reply: answered.Id, sender: "codex");
        await PostAsync(r, "is the schema final?", task: a);           // open question
        await PostAsync(r, "please look at the test", task: a, to: "codex"); // addressed, no reply
        await PostAsync(r, "build is green", task: a);                  // a statement
        await PostAsync(r, "unrelated?", sender: "codex");              // project channel, not counted

        await r.Flow.TakeAsync(a, "s1", Ct);
        var done = await r.Life.DoneAsync(a, "s1", new DoneRequest { Summary = "ok" }, Ct);

        Assert.Equal(5, done.MessageCount);
        Assert.Equal(2, done.UnansweredQuestionCount);

        var b = await TaskAsync(r, "B");
        await r.Flow.TakeAsync(b, "s1", Ct);
        var quiet = await r.Life.DoneAsync(b, "s1", new DoneRequest { Summary = "ok" }, Ct);
        Assert.Equal(0, quiet.MessageCount);
        Assert.Equal(0, quiet.UnansweredQuestionCount);
    }
}
