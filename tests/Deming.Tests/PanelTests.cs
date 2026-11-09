using System.Net;
using System.Text.RegularExpressions;
using Deming.Data;
using Deming.Data.Models;
using Deming.Data.Services;
using Deming.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace Deming.Tests;

/// <summary>The Razor panel end to end: real Kestrel, a signed-in owner, plain form posts.</summary>
public sealed partial class PanelTests : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private const string Password = "correct horse battery";

    private readonly string _path = TempDatabase.NewPath();
    private WebApplication _app = null!;
    private HttpClient _http = null!;

    public async ValueTask InitializeAsync()
    {
        _app = DemingServer.Build(["--urls=https://127.0.0.1:0"], b =>
            b.Configuration[DataServiceCollectionExtensions.DatabasePathKey] = _path, TestCertificate.Source);
        await _app.StartAsync(Ct);
        _http = TestCertificate.Client(new Uri(_app.Urls.First()), allowAutoRedirect: false, useCookies: true);
        _http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en");
        await RegisterAsync();
    }

    public async ValueTask DisposeAsync()
    {
        _http.Dispose();
        await _app.DisposeAsync();
        TempDatabase.DeleteFiles(_path, new DemingDb(_path).ConnectionString);
    }

    // ------------------------------------------------------------------ helpers

    [GeneratedRegex("name=\"__RequestVerificationToken\" value=\"([^\"]+)\"")]
    private static partial Regex TokenPattern();

    private async Task<string> GetAsync(string url)
    {
        var response = await _http.GetAsync(url, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        // Decoded: the page encodes non-ASCII text (ö -> &#xF6;), the tests compare plain text.
        return WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync(Ct));
    }

    private async Task<HttpResponseMessage> PostAsync(string url, params (string Key, string Value)[] fields)
    {
        var token = WebUtility.HtmlDecode(TokenPattern().Match(await GetAsync(url)).Groups[1].Value);
        // A browser sends the hidden _handler field of the named form; the tests name it the same way.
        var op = fields.FirstOrDefault(f => f.Key == "op").Value;
        var id = fields.FirstOrDefault(f => f.Key == "id").Value;
        var handler = url == "/register" ? "register"
            : url == "/projects/new" ? "project"
            : url.EndsWith("/edit", StringComparison.Ordinal) ? "project"
            : url == "/projects" && id is not null ? $"toggle-{id}"
            : url.StartsWith("/findings", StringComparison.Ordinal) ? "add"
            : op is null ? "create"
            : id is not null && op is "release" or "cancel" && url.StartsWith("/tasks?", StringComparison.Ordinal) ? $"{op}-{id}"
            : op;
        var form = fields.Select(f => KeyValuePair.Create(f.Key, f.Value))
            .Append(KeyValuePair.Create("__RequestVerificationToken", token))
            .Append(KeyValuePair.Create("_handler", handler));
        return await _http.PostAsync(url, new FormUrlEncodedContent(form), Ct);
    }

    private async Task RegisterAsync()
    {
        var response = await PostAsync("/register", ("username", "owner"), ("password", Password), ("confirm", Password));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }

    /// <summary>A successful post redirects to the same place with an ok message.</summary>
    private static void AssertDone(HttpResponseMessage response, string expectedOk)
    {
        Assert.True(response.StatusCode == HttpStatusCode.Redirect,
            $"{(int)response.StatusCode}: " + Regex.Match(response.Content.ReadAsStringAsync().GetAwaiter().GetResult(), "role=\"alert\">([^<]*)<").Groups[1].Value);
        Assert.Contains("ok=" + expectedOk, response.Headers.Location!.OriginalString);
    }

    private async Task<long> CreateProjectAsync(string name = "demo")
    {
        AssertDone(await PostAsync("/projects/new", ("name", name)), "ProjectCreated");
        return (await _app.Services.GetRequiredService<ProjectService>().GetByNameAsync(name, Ct))!.Id;
    }

    private async Task<long> AddTaskAsync(long project, string title, params (string, string)[] extra)
    {
        AssertDone(await PostAsync($"/tasks?project={project}", [("op", "add"), ("projectId", project.ToString()), ("title", title), .. extra]), "TaskAdded");
        var list = await _app.Services.GetRequiredService<TaskQueryService>().ListAsync(project, null, Ct);
        return list.Single(t => t.Title == title).Id;
    }

    // ------------------------------------------------------------------ access

    [Fact]
    public async Task Panel_pages_need_the_login_but_the_stylesheet_does_not()
    {
        using var anonymous = TestCertificate.Client(_http.BaseAddress!, allowAutoRedirect: false);

        foreach (var url in new[] { "/projects", "/projects/1", "/tasks", "/tasks/1", "/decisions", "/findings", "/projects/1/decisions" })
            Assert.Equal(HttpStatusCode.Redirect, (await anonymous.GetAsync(url, Ct)).StatusCode);

        var css = await anonymous.GetAsync("/panel.css", Ct);
        Assert.Equal(HttpStatusCode.OK, css.StatusCode);
        Assert.Equal("text/css", css.Content.Headers.ContentType?.MediaType);
        Assert.Contains("--accent", await css.Content.ReadAsStringAsync(Ct));

        // The stylesheet revalidates with its ETag.
        using var conditional = new HttpRequestMessage(HttpMethod.Get, "/panel.css");
        conditional.Headers.TryAddWithoutValidation("If-None-Match", css.Headers.ETag!.Tag);
        Assert.Equal(HttpStatusCode.NotModified, (await anonymous.SendAsync(conditional, Ct)).StatusCode);
    }

    [Fact]
    public async Task Posting_without_the_antiforgery_token_is_refused()
    {
        var response = await _http.PostAsync("/projects", new FormUrlEncodedContent([KeyValuePair.Create("name", "sneaky")]), Ct);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(await _app.Services.GetRequiredService<ProjectService>().GetByNameAsync("sneaky", Ct));
    }

    // ------------------------------------------------------------------ language

    [Fact]
    public async Task The_panel_is_english_by_default_and_turkish_after_the_switch()
    {
        Assert.Contains("Add project", await GetAsync("/"));

        var switched = await _http.GetAsync("/culture?c=tr&r=/projects", Ct);
        Assert.Equal(HttpStatusCode.Redirect, switched.StatusCode);
        Assert.Equal("/projects", switched.Headers.Location!.OriginalString);

        var html = await GetAsync("/projects");
        Assert.Contains("Proje Ekle", html);
        Assert.Contains("lang=\"tr\"", html);
        Assert.DoesNotContain("Add project", html);

        // The sign-in page is localized too (the cookie outlives the session).
        using var anonymous = TestCertificate.Client(_http.BaseAddress!, allowAutoRedirect: false);
        anonymous.DefaultRequestHeaders.AcceptLanguage.ParseAdd("tr");
        Assert.Contains("Giriş yap", WebUtility.HtmlDecode(await (await anonymous.GetAsync("/login", Ct)).Content.ReadAsStringAsync(Ct)));
    }

    [Fact]
    public async Task The_language_switch_never_redirects_off_site()
    {
        foreach (var target in new[] { "//evil.example", "https://evil.example", "/\\evil.example" })
        {
            var response = await _http.GetAsync("/culture?c=en&r=" + Uri.EscapeDataString(target), Ct);
            Assert.Equal("/", response.Headers.Location!.OriginalString);
        }
    }

    [Fact]
    public async Task The_theme_follows_the_system_until_the_owner_picks_one()
    {
        Assert.DoesNotContain("data-theme=", await GetAsync("/projects"));

        var dark = await _http.GetAsync("/theme?t=dark&r=/projects", Ct);
        Assert.Equal(HttpStatusCode.Redirect, dark.StatusCode);
        Assert.Equal("/projects", dark.Headers.Location!.OriginalString);
        Assert.Contains("data-theme=\"dark\"", await GetAsync("/projects"));

        // "system" removes the choice again.
        await _http.GetAsync("/theme?t=system&r=/projects", Ct);
        Assert.DoesNotContain("data-theme=", await GetAsync("/projects"));

        foreach (var target in new[] { "//evil.example", "https://evil.example" })
        {
            var response = await _http.GetAsync("/theme?t=light&r=" + Uri.EscapeDataString(target), Ct);
            Assert.Equal("/", response.Headers.Location!.OriginalString);
        }
    }

    // ------------------------------------------------------------------ projects and tasks

    [Fact]
    public async Task Projects_are_listed_and_a_duplicate_name_is_a_readable_error()
    {
        var id = await CreateProjectAsync("Alpha");
        Assert.Contains("Alpha", await GetAsync("/projects"));
        Assert.Contains($"/tasks?project={id}", await GetAsync("/projects"));

        var duplicate = await PostAsync("/projects/new", ("name", "alpha"));
        Assert.Equal(HttpStatusCode.OK, duplicate.StatusCode);
        Assert.Contains("That already exists.", await duplicate.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task The_project_list_shows_repo_path_counts_sorts_and_switches_active()
    {
        AssertDone(await PostAsync("/projects/new", ("name", "Zeta"), ("repoPath", "D:\\repo\\zeta"), ("description", "last one")), "ProjectCreated");
        var alpha = await CreateProjectAsync("Alpha");
        var zeta = (await _app.Services.GetRequiredService<ProjectService>().GetByNameAsync("Zeta", Ct))!.Id;
        await AddTaskAsync(alpha, "one");
        await AddTaskAsync(alpha, "two");

        var html = await GetAsync("/projects");
        Assert.Contains("D:\\repo\\zeta", html);
        Assert.Contains("0 / 2 done", html);
        Assert.Contains("2 queued", html);
        Assert.Contains("badge active", html);
        Assert.Contains("aria-sort=\"ascending\"", html);
        Assert.True(html.IndexOf("Alpha", StringComparison.Ordinal) < html.IndexOf("Zeta", StringComparison.Ordinal));

        // Header links sort: by name descending, and by id.
        var byNameDesc = await GetAsync("/projects?sort=name&dir=desc");
        Assert.True(byNameDesc.IndexOf("Zeta", StringComparison.Ordinal) < byNameDesc.IndexOf("Alpha", StringComparison.Ordinal));
        var byIdDesc = await GetAsync("/projects?sort=id&dir=desc");
        Assert.True(byIdDesc.IndexOf($"<td class=\"num\">{alpha}<", StringComparison.Ordinal) < byIdDesc.IndexOf($"<td class=\"num\">{zeta}<", StringComparison.Ordinal));

        // The row button makes a project inactive (no delete exists), and active again.
        var projects = _app.Services.GetRequiredService<ProjectService>();
        AssertDone(await PostAsync("/projects", ("id", zeta.ToString()), ("active", "0")), "ProjectDeactivated");
        Assert.False((await projects.GetByIdAsync(zeta, Ct))!.IsActive);
        var inactive = await GetAsync("/projects");
        Assert.Contains("class=\"inactive\"", inactive);
        Assert.Contains("Inactive", inactive);
        AssertDone(await PostAsync("/projects", ("id", zeta.ToString()), ("active", "1")), "ProjectActivated");
        Assert.True((await projects.GetByIdAsync(zeta, Ct))!.IsActive);

        // The edit page changes name, repo path and the flag.
        Assert.Contains("D:\\repo\\zeta", await GetAsync($"/projects/{zeta}/edit"));
        AssertDone(await PostAsync($"/projects/{zeta}/edit", ("name", "Omega"), ("repoPath", "D:\\repo\\omega"), ("description", "")), "ProjectSaved");
        var saved = (await projects.GetByIdAsync(zeta, Ct))!;
        Assert.Equal(("Omega", "D:\\repo\\omega", null, false), (saved.Name, saved.RepoPath, saved.Description, saved.IsActive));
        Assert.Equal(HttpStatusCode.NotFound, (await _http.GetAsync("/projects/9999/edit", Ct)).StatusCode);
    }

    [Fact]
    public async Task Tasks_can_be_added_filtered_and_searched()
    {
        var project = await CreateProjectAsync();
        var bridge = await AddTaskAsync(project, "Köprü kur", ("priority", "1"));
        var docs = await AddTaskAsync(project, "Write docs");

        // The table links each shown task; the add form's dependency picker lists titles too.
        string Row(long id) => $"<a href=\"/tasks/{id}\">";
        var list = await GetAsync($"/tasks?project={project}");
        Assert.Contains(Row(bridge), list);
        Assert.Contains(Row(docs), list);

        // Search folds diacritics and case (kopru finds Köprü).
        var found = await GetAsync($"/tasks?project={project}&q=KOPRU");
        Assert.Contains(Row(bridge), found);
        Assert.DoesNotContain(Row(docs), found);

        Assert.DoesNotContain(Row(docs), await GetAsync($"/tasks?project={project}&status=done"));
        Assert.Contains(Row(docs), await GetAsync($"/tasks?project={project}&status=queued"));

        // "Ready to take" is not a status: queued tasks with no open dependency, the rule next uses.
        var waits = await AddTaskAsync(project, "Waits for docs", ("dependsOn", docs.ToString()));
        var ready = await GetAsync($"/tasks?project={project}&status=eligible");
        Assert.Contains("<option value=\"eligible\" selected", ready);
        Assert.Contains(Row(docs), ready);
        Assert.DoesNotContain(Row(waits), ready);
        Assert.Contains(Row(waits), await GetAsync($"/tasks?status=queued"));

        var duplicate = await PostAsync($"/tasks?project={project}", ("op", "add"), ("projectId", project.ToString()), ("title", "write DOCS"));
        Assert.Equal(HttpStatusCode.OK, duplicate.StatusCode);
        Assert.Contains("already exists", await duplicate.Content.ReadAsStringAsync(Ct));

        var noProject = await PostAsync("/tasks", ("op", "add"), ("title", "Orphan"));
        Assert.Equal(HttpStatusCode.OK, noProject.StatusCode);
        Assert.Contains("Choose a project", await noProject.Content.ReadAsStringAsync(Ct));

        var missing = await _http.GetAsync("/tasks?project=9999", Ct);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task The_top_level_pages_span_projects_default_to_active_ones_and_the_old_project_pages_redirect()
    {
        var active = await CreateProjectAsync("active-one");
        var passive = await CreateProjectAsync("passive-one");
        var inActive = await AddTaskAsync(active, "Task of the active");
        var inPassive = await AddTaskAsync(passive, "Task of the passive");
        var decisions = _app.Services.GetRequiredService<DecisionService>();
        await decisions.AddAsync(active, "t", new DecisionRequest { Title = "Decision A", Text = "a" }, Ct);
        await decisions.AddAsync(passive, "t", new DecisionRequest { Title = "Decision P", Text = "p" }, Ct);
        var findings = _app.Services.GetRequiredService<FindingService>();
        await findings.AddAsync(active, "t", new FindingRequest { Title = "Finding A", Body = "a" }, Ct);
        await findings.AddAsync(passive, "t", new FindingRequest { Title = "Finding P", Body = "p" }, Ct);
        AssertDone(await PostAsync("/projects", ("id", passive.ToString()), ("active", "0")), "ProjectDeactivated");

        // Default: active projects only; "all" brings the inactive ones back; a number picks one (even inactive).
        var tasks = await GetAsync("/tasks");
        Assert.Contains($"<a href=\"/tasks/{inActive}\">", tasks);
        Assert.DoesNotContain($"<a href=\"/tasks/{inPassive}\">", tasks);
        Assert.Contains($"<a href=\"/tasks/{inPassive}\">", await GetAsync("/tasks?project=all"));
        Assert.Contains($"<a href=\"/tasks/{inPassive}\">", await GetAsync($"/tasks?project={passive}"));
        Assert.DoesNotContain($"<a href=\"/tasks/{inActive}\">", await GetAsync($"/tasks?project={passive}"));

        var decisionList = await GetAsync("/decisions");
        Assert.Contains("Decision A", decisionList);
        Assert.DoesNotContain("Decision P", decisionList);
        Assert.Contains("Decision P", await GetAsync("/decisions?project=all"));

        var findingList = await GetAsync("/findings");
        Assert.Contains("Finding A", findingList);
        Assert.DoesNotContain("Finding P", findingList);
        Assert.Contains("Finding P", await GetAsync("/findings?project=all"));

        // The menu has the four pages in order; the current one is marked, with or without ?project=.
        var menu = await GetAsync($"/findings?project={active}");
        Assert.Contains("<a href=\"/findings\" aria-current=\"page\">", menu);
        Assert.True(menu.IndexOf("href=\"/tasks\"", StringComparison.Ordinal) < menu.IndexOf("href=\"/projects\"", StringComparison.Ordinal));
        Assert.True(menu.IndexOf("href=\"/projects\"", StringComparison.Ordinal) < menu.IndexOf("href=\"/decisions\"", StringComparison.Ordinal));
        Assert.True(menu.IndexOf("href=\"/decisions\"", StringComparison.Ordinal) < menu.IndexOf("href=\"/findings\"", StringComparison.Ordinal));

        // "/" is the project list: that entry, and only that one, is marked there, as on /projects and its sub-pages.
        foreach (var path in new[] { "/", "/projects", "/projects/new" })
        {
            var page = await GetAsync(path);
            Assert.Contains("<a href=\"/projects\" aria-current=\"page\">", page);
            Assert.Equal(1, page.Split("aria-current=\"page\"").Length - 1);
        }

        // The old per-project pages redirect (302, never cached) to the filtered top-level ones.
        foreach (var (old, target) in new[]
        {
            ($"/projects/{active}", $"/tasks?project={active}"),
            ($"/projects/{active}/decisions", $"/decisions?project={active}"),
            ($"/projects/{active}/findings", $"/findings?project={active}"),
        })
        {
            var response = await _http.GetAsync(old, Ct);
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.EndsWith(target, response.Headers.Location!.OriginalString);
        }
    }

    [Fact]
    public async Task A_finding_is_added_listed_without_body_and_shown_as_markdown()
    {
        var project = await CreateProjectAsync();
        const string body = "# Heading\n<script>alert(1)</script>\n- point";
        AssertDone(await PostAsync($"/findings?project={project}", ("projectId", project.ToString()), ("title", "Measured it"), ("body", body)), "FindingAdded");
        var finding = (await _app.Services.GetRequiredService<FindingService>().ListAsync(project, null, null, Ct)).Single();

        // The list carries the title and the length, never the body.
        var list = await GetAsync($"/findings?project={project}");
        Assert.Contains("Measured it", list);
        Assert.DoesNotContain("point", list);

        // The detail renders the markdown; raw HTML in the body stays escaped text.
        var raw = await _http.GetStringAsync($"/findings?project={project}&view={finding.Id}", Ct);
        Assert.Contains("<h1>Heading</h1>", raw);
        Assert.Contains("<li>point</li>", raw);
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", raw);
        Assert.DoesNotContain("<script>alert(1)", raw);

        // A blank body is refused by the database and reported; a trace to nowhere is refused too.
        var blank = await PostAsync($"/findings?project={project}", ("projectId", project.ToString()), ("title", "No body"), ("body", " "));
        Assert.Equal(HttpStatusCode.OK, blank.StatusCode);
        var traced = await PostAsync($"/findings?project={project}", ("projectId", project.ToString()), ("title", "Bad trace"), ("body", "x"), ("taskId", "9999"));
        Assert.Equal(HttpStatusCode.OK, traced.StatusCode);
        Assert.Single(await _app.Services.GetRequiredService<FindingService>().ListAsync(project, null, null, Ct));
    }

    [Fact]
    public async Task A_task_is_edited_with_scope_and_dependencies_and_a_cycle_is_refused()
    {
        var project = await CreateProjectAsync();
        var first = await AddTaskAsync(project, "First");
        var second = await AddTaskAsync(project, "Second");

        AssertDone(await PostAsync($"/tasks/{second}", ("op", "save"), ("title", "Second renamed"),
            ("scope", "Do the second thing"), ("priority", "3"), ("dependsOn", first.ToString())), "TaskSaved");

        var detail = await GetAsync($"/tasks/{second}");
        Assert.Contains("Second renamed", detail);
        Assert.Contains("Do the second thing", detail);
        Assert.Contains($"value=\"{first}\" checked", detail);

        var cycle = await PostAsync($"/tasks/{first}", ("op", "save"), ("title", "First"), ("priority", "2"),
            ("dependsOn", second.ToString()));
        Assert.Equal(HttpStatusCode.OK, cycle.StatusCode);
        Assert.Contains("wait for each other", await cycle.Content.ReadAsStringAsync(Ct));

        // Clearing the checkboxes clears the dependencies; a blank scope clears the scope.
        AssertDone(await PostAsync($"/tasks/{second}", ("op", "save"), ("title", "Second renamed"), ("scope", ""), ("priority", "3")), "TaskSaved");
        var cleared = await _app.Services.GetRequiredService<TaskQueryService>().GetAsync(second, Ct);
        Assert.Empty(cleared.Dependencies);
        Assert.True(string.IsNullOrEmpty(cleared.Task.Scope));
    }

    [Fact]
    public async Task A_lock_is_released_and_a_task_cancelled_even_when_an_agent_holds_it()
    {
        var project = await CreateProjectAsync();
        var held = await AddTaskAsync(project, "Held by an agent");
        var tasks = _app.Services.GetRequiredService<TaskService>();
        await tasks.TakeAsync(held, "agent-session", Ct);

        // The list shows who holds it; the detail refuses an edit while the lock is live.
        Assert.Contains("agent-session", await GetAsync($"/tasks?project={project}"));
        var edit = await PostAsync($"/tasks/{held}", ("op", "save"), ("title", "Renamed"), ("priority", "2"));
        Assert.Contains("agent session holds this task", await edit.Content.ReadAsStringAsync(Ct));

        AssertDone(await PostAsync($"/tasks?project={project}", ("op", "release"), ("id", held.ToString())), "TaskRequeued");
        var released = (await _app.Services.GetRequiredService<TaskQueryService>().GetAsync(held, Ct)).Task;
        Assert.Equal(TaskItemStatus.Queued, released.Status);
        Assert.Null(released.LockOwner);

        await tasks.TakeAsync(held, "agent-session", Ct);
        AssertDone(await PostAsync($"/tasks/{held}", ("op", "cancel"), ("reason", "No longer needed")), "TaskCancelled");
        var cancelled = await _app.Services.GetRequiredService<TaskQueryService>().GetAsync(held, Ct);
        Assert.Equal(TaskItemStatus.Cancelled, cancelled.Task.Status);
        Assert.Equal("No longer needed", cancelled.Task.CancelledReason);

        var page = await GetAsync($"/tasks/{held}");
        Assert.Contains("No longer needed", page);
        Assert.Contains("can no longer be edited", page);
    }

    [Fact]
    public async Task A_cancel_needs_a_reason_and_the_panel_cannot_start_a_task()
    {
        var project = await CreateProjectAsync();
        var id = await AddTaskAsync(project, "Queued one");

        var blank = await PostAsync($"/tasks/{id}", ("op", "cancel"), ("reason", " "));
        Assert.Equal(HttpStatusCode.OK, blank.StatusCode);
        Assert.Contains("not valid", await blank.Content.ReadAsStringAsync(Ct));

        // No form field or op moves a task to in progress.
        await PostAsync($"/tasks/{id}", ("op", "save"), ("title", "Queued one"), ("priority", "2"), ("status", "in_progress"));
        Assert.Equal(TaskItemStatus.Queued, (await _app.Services.GetRequiredService<TaskQueryService>().GetAsync(id, Ct)).Task.Status);
        Assert.Contains("cannot be moved to in progress", await GetAsync($"/tasks/{id}"));
    }

    [Fact]
    public async Task The_detail_shows_the_handoff_notes_of_the_task()
    {
        var project = await CreateProjectAsync();
        var id = await AddTaskAsync(project, "Finish me");
        await _app.Services.GetRequiredService<TaskService>().TakeAsync(id, "worker-1", Ct);
        await _app.Services.GetRequiredService<TaskLifecycleService>().DoneAsync(id, "worker-1", new DoneRequest
        {
            Summary = "Built the thing", Caveats = "Mind the cache", TouchedFiles = "src/A.cs", CommitHash = "abc1234",
        }, Ct);

        var page = await GetAsync($"/tasks/{id}");
        Assert.Contains("Built the thing", page);
        Assert.Contains("Mind the cache", page);
        Assert.Contains("src/A.cs", page);
        Assert.Contains("abc1234", page);
    }

    // ------------------------------------------------------------------ decisions

    [Fact]
    public async Task Decisions_are_added_read_with_rationale_and_corrected_by_superseding()
    {
        var project = await CreateProjectAsync();
        var url = $"/decisions?project={project}";
        var service = _app.Services.GetRequiredService<DecisionService>();

        AssertDone(await PostAsync(url, ("op", "add"), ("projectId", project.ToString()), ("title", "Use SQLite"), ("text", "SQLite in WAL mode."),
            ("rationale", "No server to install.")), "DecisionAdded");
        var original = (await service.ListAsync(project, null, Ct)).Single();

        var list = await GetAsync(url);
        Assert.Contains("Use SQLite", list);
        Assert.DoesNotContain("No server to install.", list); // the rationale is fetched by id only
        Assert.Contains("No server to install.", await GetAsync($"{url}&view={original.Id}"));

        var correction = await PostAsync($"{url}&view={original.Id}", ("op", "supersede"), ("id", original.Id.ToString()),
            ("title", "Use SQLite"), ("text", "SQLite in WAL mode, one file."), ("rationale", "Corrected wording."));
        AssertDone(correction, "DecisionSuperseded");

        var all = await service.ListAsync(project, null, Ct);
        Assert.Equal(2, all.Count);
        var old = all.Single(d => d.Id == original.Id);
        Assert.Equal(DecisionStatus.Superseded, old.Status);
        Assert.NotNull(old.SupersededById);
        Assert.Single(await service.ListAsync(project, DecisionStatus.Active, Ct));

        // A superseded decision offers no form any more; a stale post is refused and writes nothing.
        Assert.DoesNotContain("name=\"_handler\" value=\"supersede\"", await GetAsync($"{url}&view={original.Id}"));
        var replacement = all.Single(d => d.Id != original.Id);
        var token = TokenPattern().Match(await GetAsync($"{url}&view={replacement.Id}")).Groups[1].Value;
        var stale = await _http.PostAsync($"{url}&view={original.Id}", new FormUrlEncodedContent(
        [
            KeyValuePair.Create("__RequestVerificationToken", WebUtility.HtmlDecode(token)),
            KeyValuePair.Create("_handler", "supersede"), KeyValuePair.Create("op", "supersede"),
            KeyValuePair.Create("id", original.Id.ToString()), KeyValuePair.Create("title", "x"), KeyValuePair.Create("text", "y"),
        ]), Ct);
        Assert.NotEqual(HttpStatusCode.Redirect, stale.StatusCode);
        Assert.Equal(2, (await service.ListAsync(project, null, Ct)).Count);

        // No delete route exists.
        Assert.NotEqual(HttpStatusCode.OK, (await _http.DeleteAsync($"/decisions/{original.Id}", Ct)).StatusCode);
    }

    [Fact]
    public async Task A_decision_over_the_length_cap_is_refused_by_the_database_and_reported()
    {
        var project = await CreateProjectAsync();
        var response = await PostAsync($"/decisions?project={project}", ("op", "add"), ("projectId", project.ToString()), ("title", "Long"),
            ("text", new string('x', NoteLimits.DecisionTextMax + 1)));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("empty or too long", await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task Decision_filter_and_foreign_ids_behave()
    {
        var project = await CreateProjectAsync("one");
        var other = await CreateProjectAsync("two");
        AssertDone(await PostAsync($"/decisions?project={other}", ("op", "add"), ("projectId", other.ToString()), ("title", "Elsewhere"), ("text", "t")), "DecisionAdded");

        // The list is limited by the project filter and the status.
        Assert.DoesNotContain("Elsewhere", await GetAsync($"/decisions?project={project}"));
        Assert.Contains("Elsewhere", await GetAsync($"/decisions?project={other}&status=active"));
        Assert.DoesNotContain("Elsewhere", await GetAsync($"/decisions?project={other}&status=superseded"));
        Assert.Contains("Elsewhere", await GetAsync("/decisions"));
    }
}
