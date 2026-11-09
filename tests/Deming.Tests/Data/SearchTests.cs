using Deming.Data;
using Deming.Data.Models;
using Deming.Data.Services;
using Microsoft.Extensions.Options;

namespace Deming.Tests.Data;

/// <summary>Search over findings, decisions, notes and tasks: terms, folding, filters, snippets.</summary>
public class SearchTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Rig(TempDatabase t) : IAsyncDisposable
    {
        public TempDatabase Temp => t;
        public ProjectService Projects { get; } = new(t.Db);
        public TaskService Flow { get; } = new(t.Db, TimeProvider.System, Options.Create(new TaskLockOptions()));
        public TaskLifecycleService Life { get; } = new(t.Db, TimeProvider.System, Options.Create(new TaskLockOptions()));
        public DecisionService Decisions { get; } = new(t.Db, TimeProvider.System);
        public FindingService Findings { get; } = new(t.Db);
        public SearchService Search { get; } = new(t.Db);
        public ValueTask DisposeAsync() => t.DisposeAsync();
    }

    private static async Task<Rig> NewRigAsync() => new(await TempDatabase.CreateAsync());

    private static Task<SearchResponse> InProject(Rig r, long project, string q, string? type = null, int? top = null) =>
        r.Search.SearchAsync(q, project, false, type, top, Ct);

    [Fact]
    public async Task Every_kind_is_searched_in_its_own_fields()
    {
        await using var r = await NewRigAsync();
        var p = (await r.Projects.CreateAsync("Alpha", ct: Ct)).Id;

        var task = await r.Life.AddAsync(p, "s", new AddTaskRequest { Title = "Port the importer", Scope = "Read the export and write rows" }, Ct);
        var finding = await r.Findings.AddAsync(p, "s", new FindingRequest { Title = "Latency table", Body = "p95 of next is 12 ms on the zebra rig" }, Ct);
        var decision = await r.Decisions.AddAsync(p, "s", new DecisionRequest { Title = "Keep SQLite", Text = "One file", Rationale = "The zebra rig has no server" }, Ct);
        await r.Flow.TakeAsync(task.Id, "s", Ct);
        var done = await r.Life.DoneAsync(task.Id, "s", new DoneRequest { Summary = "Importer ported", Caveats = "watch the zebra column", TouchedFiles = "src/Importer.cs" }, Ct);

        var hits = (await InProject(r, p, "zebra")).Results;
        Assert.Equal(3, hits.Count);
        Assert.Contains(hits, h => h is { Type: SearchTypes.Finding, MatchedIn: "body" } && h.Id == finding.Id);
        Assert.Contains(hits, h => h is { Type: SearchTypes.Decision, MatchedIn: "rationale", Status: "active" } && h.Id == decision.Id);
        var note = Assert.Single(hits, h => h.Type == SearchTypes.Note);
        Assert.Equal((done.Note.Id, task.Id, "Port the importer", "caveats", "handoff"), (note.Id, note.TaskId, note.Title, note.MatchedIn, note.Status));

        var taskHit = Assert.Single((await InProject(r, p, "export rows")).Results);
        Assert.Equal((SearchTypes.Task, task.Id, "scope", "done"), (taskHit.Type, taskHit.Id, taskHit.MatchedIn, taskHit.Status));

        Assert.Equal("touchedFiles", Assert.Single((await InProject(r, p, "importer.cs")).Results).MatchedIn);
        Assert.Equal("title", Assert.Single((await InProject(r, p, "latency")).Results).MatchedIn);
    }

    [Fact]
    public async Task Messages_are_found_by_body_and_carry_sender_and_channel()
    {
        await using var r = await NewRigAsync();
        var messages = new MessageService(r.Temp.Db, TimeProvider.System);
        var p = (await r.Projects.CreateAsync("Alpha", ct: Ct)).Id;
        var other = (await r.Projects.CreateAsync("Beta", ct: Ct)).Id;
        var task = await r.Life.AddAsync(p, "s", new AddTaskRequest { Title = "Wire the bridge" }, Ct);

        var onProject = await messages.PostAsync("s", new MessageRequest { Project = p, Sender = "Claude", Body = "Is the zebra cache warm?" }, Ct);
        var onTask = await messages.PostAsync("s", new MessageRequest { Project = p, TaskId = task.Id, Sender = "codex", Body = "Köprü is ready, zebra ok" }, Ct);
        await messages.PostAsync("s", new MessageRequest { Project = other, Sender = "claude", Body = "zebra elsewhere" }, Ct);

        var hits = (await InProject(r, p, "ZEBRA")).Results;
        Assert.Equal([onTask.Id, onProject.Id], hits.Select(h => h.Id));
        Assert.All(hits, h => Assert.Equal((SearchTypes.Message, "body", p), (h.Type, h.MatchedIn, h.ProjectId)));

        var project = hits.Single(h => h.Id == onProject.Id);
        Assert.Equal(("claude", "project", null), (project.Title, project.Status, project.TaskId));
        Assert.Contains("zebra cache", project.Snippet);

        var task_ = hits.Single(h => h.Id == onTask.Id);
        Assert.Equal(("codex", $"task-{task.Id}", task.Id), (task_.Title, task_.Status, task_.TaskId));

        // The sender's label is not searched; the type filter and folding work like for the other kinds.
        Assert.Empty((await InProject(r, p, "codex")).Results);
        Assert.Equal(onTask.Id, Assert.Single((await InProject(r, p, "kopru", SearchTypes.Message)).Results).Id);
        Assert.Empty((await InProject(r, p, "zebra", SearchTypes.Finding)).Results);
        Assert.Equal(3, (await r.Search.SearchAsync("zebra", null, true, "message", null, Ct)).Results.Count);
    }

    [Fact]
    public async Task Block_and_cancel_reasons_are_found_as_notes()
    {
        await using var r = await NewRigAsync();
        var p = (await r.Projects.CreateAsync("Alpha", ct: Ct)).Id;
        var blocked = await r.Life.AddAsync(p, "s", new AddTaskRequest { Title = "A" }, Ct);
        var cancelled = await r.Life.AddAsync(p, "s", new AddTaskRequest { Title = "B" }, Ct);
        await r.Flow.TakeAsync(blocked.Id, "s", Ct);
        await r.Life.BlockAsync(blocked.Id, "s", "waiting for the vendor key", Ct);
        await r.Life.CancelAsync(cancelled.Id, "s", "vendor dropped the product", Ct);

        var hits = (await InProject(r, p, "vendor", SearchTypes.Note)).Results;

        Assert.Equal([cancelled.Id, blocked.Id], hits.Select(h => h.TaskId!.Value));
        Assert.All(hits, h => Assert.Equal("summary", h.MatchedIn));
    }

    [Fact]
    public async Task All_terms_must_occur_folded_for_case_turkish_i_and_accents()
    {
        await using var r = await NewRigAsync();
        var p = (await r.Projects.CreateAsync("Alpha", ct: Ct)).Id;
        await r.Findings.AddAsync(p, null, new FindingRequest { Title = "DRIFT ölçümü", Body = "Köprü kırıldı; İstanbul hattı" }, Ct);

        Assert.Single((await InProject(r, p, "drift")).Results);
        Assert.Single((await InProject(r, p, "kopru OLCUMU")).Results);
        Assert.Single((await InProject(r, p, "istanbul kirildi")).Results);
        Assert.Single((await InProject(r, p, "ISTANBUL")).Results);
        Assert.Empty((await InProject(r, p, "drift ankara")).Results);
        // Terms are matched as text, not patterns.
        Assert.Empty((await InProject(r, p, "dr%t")).Results);
        Assert.Empty((await InProject(r, p, "dr_ft")).Results);
    }

    [Fact]
    public async Task Project_all_type_and_top_narrow_the_results()
    {
        await using var r = await NewRigAsync();
        var a = (await r.Projects.CreateAsync("Alpha", ct: Ct)).Id;
        var b = (await r.Projects.CreateAsync("Beta", ct: Ct)).Id;
        for (var i = 0; i < 3; i++)
            await r.Findings.AddAsync(a, null, new FindingRequest { Title = $"cache {i}", Body = "warm" }, Ct);
        await r.Life.AddAsync(a, null, new AddTaskRequest { Title = "cache task" }, Ct);
        await r.Findings.AddAsync(b, null, new FindingRequest { Title = "cache in beta", Body = "cold" }, Ct);
        await r.Projects.UpdateAsync(b, new ProjectChanges(IsActive: false), Ct);

        Assert.Equal(4, (await InProject(r, a, "cache")).Results.Count);
        Assert.Equal(3, (await InProject(r, a, "cache", "FINDING")).Results.Count);
        Assert.Single((await InProject(r, b, "cache")).Results);

        // all=true searches inactive projects too.
        var all = await r.Search.SearchAsync("cache", null, true, null, null, Ct);
        Assert.Equal(5, all.Results.Count);
        Assert.Contains(all.Results, h => h.ProjectName == "Beta");
        Assert.False(all.More);

        var two = await InProject(r, a, "cache", top: 2);
        Assert.Equal(2, two.Results.Count);
        Assert.True(two.More);
        Assert.True(two.Results[0].CreatedAt >= two.Results[1].CreatedAt); // newest first
    }

    [Theory]
    [InlineData("  ", 1L, false, null, null)]
    [InlineData("x", null, false, null, null)]
    [InlineData("x", 1L, true, null, null)]
    [InlineData("x", 1L, false, "chat", null)]
    [InlineData("x", 1L, false, null, 0)]
    [InlineData("x", 1L, false, null, 101)]
    public async Task Bad_input_is_refused(string q, long? project, bool all, string? type, int? top)
    {
        await using var r = await NewRigAsync();
        await r.Projects.CreateAsync("Alpha", ct: Ct);

        var ex = await Assert.ThrowsAsync<TaskFlowException>(() => r.Search.SearchAsync(q, project, all, type, top, Ct));
        Assert.Equal(TaskFlowError.InvalidInput, ex.Error);
    }

    [Fact]
    public async Task An_unknown_project_is_not_found()
    {
        await using var r = await NewRigAsync();
        var ex = await Assert.ThrowsAsync<TaskFlowException>(() => r.Search.SearchAsync("x", 42, false, null, null, Ct));
        Assert.Equal(TaskFlowError.NotFound, ex.Error);
    }

    [Fact]
    public async Task The_snippet_is_cut_around_the_match_and_never_the_whole_body()
    {
        await using var r = await NewRigAsync();
        var p = (await r.Projects.CreateAsync("Alpha", ct: Ct)).Id;
        var body = string.Join(' ', Enumerable.Repeat("filler", 100)) + "\n\nthe Köprü\tmeasurement\n\n" + string.Join(' ', Enumerable.Repeat("tail", 100));
        await r.Findings.AddAsync(p, null, new FindingRequest { Title = "Long", Body = body }, Ct);

        var hit = Assert.Single((await InProject(r, p, "kopru")).Results);

        Assert.StartsWith("…", hit.Snippet);
        Assert.EndsWith("…", hit.Snippet);
        Assert.Contains("the Köprü measurement tail", hit.Snippet);
        Assert.InRange(hit.Snippet.Length, 100, SearchService.SnippetLength + 2);
    }

    [Theory]
    [InlineData("short text", 6, "short text")]
    [InlineData("", 0, "")]
    public void Snippet_of_short_text_is_the_text(string text, int position, string expected) =>
        Assert.Equal(expected, SearchService.Snippet(text, position));

    [Fact]
    public void Fold_keeps_the_length_and_matches_normalize()
    {
        const string text = "İstanbul\tKÖPRÜ  ılık Çay";
        var folded = TextNormalizer.FoldKeepingLength(text);

        Assert.Equal(text.Length, folded.Length);
        Assert.Equal("istanbul kopru  ilik cay", folded);
        Assert.Equal(TextNormalizer.Normalize(text), TextNormalizer.Normalize(folded));
    }

    [Fact]
    public void Terms_are_split_folded_and_deduplicated() =>
        Assert.Equal(["kopru", "drift"], SearchService.Terms("  Köprü\tDRIFT kopru "));
}
