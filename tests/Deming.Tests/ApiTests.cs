using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Deming.Data;
using Deming.Server;
using Deming.Server.Api;
using Microsoft.AspNetCore.Builder;

namespace Deming.Tests;

/// <summary>
/// The API over real HTTP: Kestrel on a free loopback port, a real HttpClient, a temporary database
/// and a generated API key (no key is ever written to a file).
/// </summary>
public sealed class ApiTests : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _path = TempDatabase.NewPath();
    private readonly string _key = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
    private WebApplication _app = null!;
    private HttpClient _http = null!;

    public async ValueTask InitializeAsync() => await StartAsync(_key);

    private async Task StartAsync(string? key)
    {
        _app = DemingServer.Build(["--urls=https://127.0.0.1:0"], b =>
        {
            b.Configuration[DataServiceCollectionExtensions.DatabasePathKey] = _path;
            if (key is not null)
                b.Configuration[ApiKeySettings.ConfigKey] = key;
        }, TestCertificate.Source);
        await _app.StartAsync(Ct);
        _http = TestCertificate.Client(new Uri(_app.Urls.First()));
        if (key is not null)
            _http.DefaultRequestHeaders.Add(ApiKeySettings.HeaderName, key);
    }

    public async ValueTask DisposeAsync()
    {
        _http.Dispose();
        await _app.DisposeAsync();
        TempDatabase.DeleteFiles(_path, new DemingDb(_path).ConnectionString);
    }

    // ------------------------------------------------------------------ helpers

    private async Task<(HttpStatusCode Status, JsonElement Body)> Send(
        HttpMethod method, string url, object? body = null, string? session = null, string? rawBody = null)
    {
        using var request = new HttpRequestMessage(method, url);
        if (rawBody is not null)
            request.Content = new StringContent(rawBody, Encoding.UTF8, "application/json");
        else if (body is not null)
            request.Content = JsonContent.Create(body);
        if (session is not null)
            request.Headers.Add(ApiKeySettings.SessionHeaderName, session);
        using var response = await _http.SendAsync(request, Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        return (response.StatusCode, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone());
    }

    private static string Error(JsonElement body) => body.GetProperty("error").GetString()!;

    private async Task<long> NewProject(string name = "demo")
    {
        var (status, body) = await Send(HttpMethod.Post, "/api/projects", new { name });
        Assert.Equal(HttpStatusCode.Created, status);
        return body.GetProperty("id").GetInt64();
    }

    private async Task<long> AddTask(long project, string title, object? extra = null, string session = "s1")
    {
        var payload = new Dictionary<string, object?> { ["title"] = title };
        if (extra is not null)
            foreach (var p in extra.GetType().GetProperties())
                payload[p.Name] = p.GetValue(extra);
        var (status, body) = await Send(HttpMethod.Post, $"/api/projects/{project}/tasks", payload, session);
        Assert.Equal(HttpStatusCode.Created, status);
        return body.GetProperty("id").GetInt64();
    }

    // ------------------------------------------------------------------ the API key

    [Fact]
    public async Task Missing_and_wrong_keys_are_401_with_a_reason()
    {
        using var anonymous = TestCertificate.Client(_http.BaseAddress!);
        var none = await anonymous.GetAsync("/api/projects", Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, none.StatusCode);
        Assert.Equal("missing_api_key", JsonDocument.Parse(await none.Content.ReadAsStringAsync(Ct)).RootElement.GetProperty("error").GetString());

        anonymous.DefaultRequestHeaders.Add(ApiKeySettings.HeaderName, _key + "x");
        var wrong = await anonymous.GetAsync("/api/projects", Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal("invalid_api_key", JsonDocument.Parse(await wrong.Content.ReadAsStringAsync(Ct)).RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task A_project_is_edited_made_inactive_and_then_refuses_next_take_and_add()
    {
        var (created, project) = await Send(HttpMethod.Post, "/api/projects", new { name = "demo", repoPath = "D:\\repo\\demo" });
        Assert.Equal(HttpStatusCode.Created, created);
        Assert.Equal("D:\\repo\\demo", project.GetProperty("repoPath").GetString());
        Assert.True(project.GetProperty("isActive").GetBoolean());
        var id = project.GetProperty("id").GetInt64();
        var task = await AddTask(id, "first");

        var (patched, edited) = await Send(HttpMethod.Patch, $"/api/projects/{id}", new { description = "d", isActive = false });
        Assert.Equal(HttpStatusCode.OK, patched);
        Assert.Equal("D:\\repo\\demo", edited.GetProperty("repoPath").GetString());
        Assert.False(edited.GetProperty("isActive").GetBoolean());
        Assert.Equal(HttpStatusCode.NotFound, (await Send(HttpMethod.Patch, "/api/projects/999", new { name = "x" })).Status);

        foreach (var (method, url, body) in new (HttpMethod, string, object?)[]
        {
            (HttpMethod.Post, $"/api/projects/{id}/tasks/next", null),
            (HttpMethod.Post, $"/api/tasks/{task}/take", null),
            (HttpMethod.Post, $"/api/projects/{id}/tasks", new { title = "second" }),
        })
        {
            var (status, error) = await Send(method, url, body, session: "s1");
            Assert.Equal(HttpStatusCode.Conflict, status);
            Assert.Equal("project_inactive", Error(error));
        }

        // The list carries the counts and the flag.
        var (_, list) = await Send(HttpMethod.Get, "/api/projects");
        Assert.Equal(1, list[0].GetProperty("totalTasks").GetInt32());
        Assert.False(list[0].GetProperty("isActive").GetBoolean());

        await Send(HttpMethod.Patch, $"/api/projects/{id}", new { isActive = true });
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Post, $"/api/projects/{id}/tasks/next", session: "s1")).Status);
    }

    [Fact]
    public async Task Unknown_routes_are_a_json_404_behind_the_key_and_the_panel_login_does_not_apply()
    {
        var (status, body) = await Send(HttpMethod.Get, "/api/nope");
        Assert.Equal(HttpStatusCode.NotFound, status);
        Assert.Equal("not_found", Error(body));

        // The panel stays behind its own login: the API key does not open it.
        using var noRedirect = TestCertificate.Client(_http.BaseAddress!, allowAutoRedirect: false);
        noRedirect.DefaultRequestHeaders.Add(ApiKeySettings.HeaderName, _key);
        var panel = await noRedirect.GetAsync("/", Ct);
        Assert.Equal(HttpStatusCode.Redirect, panel.StatusCode);
    }

    [Fact]
    public async Task Without_a_configured_key_the_api_refuses_everything()
    {
        _http.Dispose();
        await _app.DisposeAsync();
        TempDatabase.DeleteFiles(_path, new DemingDb(_path).ConnectionString);
        await StartAsync(null);
        _http.DefaultRequestHeaders.Add(ApiKeySettings.HeaderName, "anything");

        var (status, body) = await Send(HttpMethod.Get, "/api/projects");
        Assert.Equal(HttpStatusCode.Unauthorized, status);
        Assert.Equal("api_key_not_configured", Error(body));
    }

    // ------------------------------------------------------------------ projects

    [Fact]
    public async Task Projects_create_get_find_by_name_and_refuse_duplicates()
    {
        var id = await NewProject("Demo App");

        var (dup, dupBody) = await Send(HttpMethod.Post, "/api/projects", new { name = "demo app" });
        Assert.Equal(HttpStatusCode.Conflict, dup);
        Assert.Equal("project_exists", Error(dupBody));

        var (blank, blankBody) = await Send(HttpMethod.Post, "/api/projects", new { name = "  " });
        Assert.Equal(HttpStatusCode.BadRequest, blank);
        Assert.Equal("invalid_input", Error(blankBody));

        var (_, byName) = await Send(HttpMethod.Get, "/api/projects?name=Demo%20App");
        Assert.Equal(id, byName[0].GetProperty("id").GetInt64());
        var (_, none) = await Send(HttpMethod.Get, "/api/projects?name=other");
        Assert.Equal(0, none.GetArrayLength());

        Assert.Equal(HttpStatusCode.NotFound, (await Send(HttpMethod.Get, "/api/projects/999")).Status);
    }

    // ------------------------------------------------------------------ the task flow

    [Fact]
    public async Task Next_done_flow_with_context_and_empty_queue()
    {
        var project = await NewProject();
        var first = await AddTask(project, "First", new { scope = "secret scope text" });
        await Send(HttpMethod.Post, $"/api/projects/{project}/decisions", new { title = "D", text = "use sqlite", rationale = "because" }, "s1");

        // next needs a session
        var (noSession, noSessionBody) = await Send(HttpMethod.Post, $"/api/projects/{project}/tasks/next");
        Assert.Equal(HttpStatusCode.BadRequest, noSession);
        Assert.Equal("session_required", Error(noSessionBody));

        var (status, next) = await Send(HttpMethod.Post, $"/api/projects/{project}/tasks/next", session: "s1");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(first, next.GetProperty("task").GetProperty("id").GetInt64());
        Assert.Equal("in_progress", next.GetProperty("task").GetProperty("status").GetString());
        Assert.False(next.GetProperty("resumed").GetBoolean());
        var decision = next.GetProperty("decisions")[0];
        Assert.Equal("use sqlite", decision.GetProperty("text").GetString());
        Assert.False(decision.TryGetProperty("rationale", out _));

        var (_, again) = await Send(HttpMethod.Post, $"/api/projects/{project}/tasks/next", session: "s1");
        Assert.True(again.GetProperty("resumed").GetBoolean());

        // done without a summary: 400, then fine
        var (noSummary, noSummaryBody) = await Send(HttpMethod.Post, $"/api/tasks/{first}/done", new { summary = "" }, "s1");
        Assert.Equal(HttpStatusCode.BadRequest, noSummary);
        Assert.Equal("invalid_input", Error(noSummaryBody));
        var (done, doneBody) = await Send(HttpMethod.Post, $"/api/tasks/{first}/done", new { summary = "did it", commitHash = "abc1234" }, "s1");
        Assert.Equal(HttpStatusCode.OK, done);
        Assert.Equal("done", doneBody.GetProperty("task").GetProperty("status").GetString());
        Assert.Equal("did it", doneBody.GetProperty("note").GetProperty("summary").GetString());

        // nothing left: 204, not an error
        var (empty, emptyBody) = await Send(HttpMethod.Post, $"/api/projects/{project}/tasks/next", session: "s1");
        Assert.Equal(HttpStatusCode.NoContent, empty);
        Assert.Equal(JsonValueKind.Undefined, emptyBody.ValueKind);
    }

    [Fact]
    public async Task List_has_no_scope_and_shows_dependencies_and_lock_owner()
    {
        var project = await NewProject();
        var a = await AddTask(project, "A", new { scope = "hidden" });
        await AddTask(project, "B", new { dependsOn = new[] { a }, priority = 1, source = "human" });
        await Send(HttpMethod.Post, $"/api/projects/{project}/tasks/next", session: "owner-1");

        var (status, list) = await Send(HttpMethod.Get, $"/api/projects/{project}/tasks");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(2, list.GetArrayLength());
        foreach (var row in list.EnumerateArray())
        {
            Assert.False(row.TryGetProperty("scope", out _));
            foreach (var field in new[] { "sortOrder", "id", "status", "priority", "source", "openDependencyCount", "lockOwner", "title" })
                Assert.True(row.TryGetProperty(field, out _), field);
        }
        var rowB = list.EnumerateArray().Single(r => r.GetProperty("title").GetString() == "B");
        Assert.Equal(1, rowB.GetProperty("openDependencyCount").GetInt32());
        Assert.Equal("human", rowB.GetProperty("source").GetString());
        var rowA = list.EnumerateArray().Single(r => r.GetProperty("title").GetString() == "A");
        Assert.Equal("owner-1", rowA.GetProperty("lockOwner").GetString());

        var (_, inProgress) = await Send(HttpMethod.Get, $"/api/projects/{project}/tasks?status=in_progress");
        Assert.Equal(1, inProgress.GetArrayLength());
        var (bad, badBody) = await Send(HttpMethod.Get, $"/api/projects/{project}/tasks?status=bogus");
        Assert.Equal(HttpStatusCode.BadRequest, bad);
        Assert.Equal("invalid_input", Error(badBody));
        Assert.Equal(HttpStatusCode.NotFound, (await Send(HttpMethod.Get, "/api/projects/999/tasks")).Status);
    }

    [Fact]
    public async Task Eligible_lists_what_next_could_take_in_queue_order()
    {
        var project = await NewProject();
        var a = await AddTask(project, "A", new { priority = 3 });
        var b = await AddTask(project, "B", new { dependsOn = new[] { a } });
        var c = await AddTask(project, "C", new { priority = 1 });
        var d = await AddTask(project, "D");
        await Send(HttpMethod.Post, $"/api/tasks/{d}/take", session: "s1");
        var e = await AddTask(project, "E");
        await Send(HttpMethod.Post, $"/api/tasks/{e}/take", session: "s1");
        await Send(HttpMethod.Post, $"/api/tasks/{e}/block", new { reason = "waits" }, "s1");

        long[] Ids(JsonElement list) => list.EnumerateArray().Select(r => r.GetProperty("id").GetInt64()).ToArray();

        var (status, eligible) = await Send(HttpMethod.Get, $"/api/projects/{project}/tasks/eligible");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal([c, a], Ids(eligible)); // B waits for A, D is in progress, E is blocked
        Assert.False(eligible[0].TryGetProperty("scope", out _));
        Assert.Equal(0, eligible[0].GetProperty("openDependencyCount").GetInt32());

        // The same rule as next: once A is done, B joins.
        await Send(HttpMethod.Post, $"/api/tasks/{a}/take", session: "s1");
        await Send(HttpMethod.Post, $"/api/tasks/{a}/done", new { summary = "A done" }, "s1");
        Assert.Equal([c, b], Ids((await Send(HttpMethod.Get, $"/api/projects/{project}/tasks/eligible")).Body));
        var (_, next) = await Send(HttpMethod.Post, $"/api/projects/{project}/tasks/next", session: "s2");
        Assert.Equal(c, next.GetProperty("task").GetProperty("id").GetInt64());

        // An inactive project has nothing to take; an unknown one is 404.
        await Send(HttpMethod.Patch, $"/api/projects/{project}", new { isActive = false });
        Assert.Equal(0, (await Send(HttpMethod.Get, $"/api/projects/{project}/tasks/eligible")).Body.GetArrayLength());
        Assert.Equal(HttpStatusCode.NotFound, (await Send(HttpMethod.Get, "/api/projects/999/tasks/eligible")).Status);
    }

    [Fact]
    public async Task Get_returns_scope_dependencies_and_notes()
    {
        var project = await NewProject();
        var a = await AddTask(project, "A");
        var b = await AddTask(project, "B", new { scope = "details", dependsOn = new[] { a } });
        await Send(HttpMethod.Post, $"/api/tasks/{a}/take", session: "s1");
        await Send(HttpMethod.Post, $"/api/tasks/{a}/done", new { summary = "A done" }, "s1");

        var (status, detail) = await Send(HttpMethod.Get, $"/api/tasks/{b}");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("details", detail.GetProperty("task").GetProperty("scope").GetString());
        Assert.Equal(a, detail.GetProperty("dependencies")[0].GetProperty("id").GetInt64());
        Assert.Equal(0, detail.GetProperty("notes").GetArrayLength());

        var (_, aDetail) = await Send(HttpMethod.Get, $"/api/tasks/{a}");
        Assert.Equal("A done", aDetail.GetProperty("notes")[0].GetProperty("summary").GetString());

        var (missing, missingBody) = await Send(HttpMethod.Get, "/api/tasks/9999");
        Assert.Equal(HttpStatusCode.NotFound, missing);
        Assert.Equal("not_found", Error(missingBody));
    }

    // ------------------------------------------------------------------ refusals

    [Fact]
    public async Task Duplicate_title_is_409_with_the_existing_id()
    {
        var project = await NewProject();
        var first = await AddTask(project, "Fix the Bridge");

        var (status, body) = await Send(HttpMethod.Post, $"/api/projects/{project}/tasks", new { title = "  fix THE bridge " }, "s2");
        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal("duplicate_title", Error(body));
        Assert.Equal(first, body.GetProperty("existingTaskId").GetInt64());
    }

    [Fact]
    public async Task Dependencies_pending_cycles_and_invalid_references()
    {
        var project = await NewProject();
        var a = await AddTask(project, "A");
        var b = await AddTask(project, "B", new { dependsOn = new[] { a } });

        var (take, takeBody) = await Send(HttpMethod.Post, $"/api/tasks/{b}/take", session: "s1");
        Assert.Equal(HttpStatusCode.Conflict, take);
        Assert.Equal("dependencies_pending", Error(takeBody));
        Assert.Equal(a, takeBody.GetProperty("pendingDependencies")[0].GetProperty("id").GetInt64());

        var (cycle, cycleBody) = await Send(HttpMethod.Patch, $"/api/tasks/{a}", new { dependsOn = new[] { b } }, "s1");
        Assert.Equal(HttpStatusCode.Conflict, cycle);
        Assert.Equal("dependency_cycle", Error(cycleBody));

        var (invalid, invalidBody) = await Send(HttpMethod.Patch, $"/api/tasks/{a}", new { dependsOn = new[] { 9999L } }, "s1");
        Assert.Equal(HttpStatusCode.BadRequest, invalid);
        Assert.Equal("invalid_dependency", Error(invalidBody));

        var (withStatus, withStatusBody) = await Send(HttpMethod.Patch, $"/api/tasks/{a}", new { status = "done" }, "s1");
        Assert.Equal(HttpStatusCode.BadRequest, withStatus);
        Assert.Equal("status_not_updatable", Error(withStatusBody));
    }

    [Fact]
    public async Task Locks_requeue_block_cancel_and_takeover()
    {
        var project = await NewProject();
        var t = await AddTask(project, "T");
        await Send(HttpMethod.Post, $"/api/tasks/{t}/take", session: "s1");

        // another session cannot take, requeue, block, or close it
        var (take, takeBody) = await Send(HttpMethod.Post, $"/api/tasks/{t}/take", session: "s2");
        Assert.Equal(HttpStatusCode.Conflict, take);
        Assert.Equal("locked_by_other_session", Error(takeBody));
        Assert.Equal(HttpStatusCode.Conflict, (await Send(HttpMethod.Post, $"/api/tasks/{t}/requeue", session: "s2")).Status);
        Assert.Equal(HttpStatusCode.Conflict, (await Send(HttpMethod.Post, $"/api/tasks/{t}/block", new { reason = "x" }, "s2")).Status);
        Assert.Equal(HttpStatusCode.Conflict, (await Send(HttpMethod.Post, $"/api/tasks/{t}/done", new { summary = "x" }, "s2")).Status);

        // force requeue works and a queued task cannot be requeued again
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Post, $"/api/tasks/{t}/requeue?force=true", session: "s2")).Status);
        var (again, againBody) = await Send(HttpMethod.Post, $"/api/tasks/{t}/requeue", session: "s2");
        Assert.Equal(HttpStatusCode.Conflict, again);
        Assert.Equal("not_requeueable", Error(againBody));

        // block needs a reason; blocked twice is a conflict
        var (noReason, noReasonBody) = await Send(HttpMethod.Post, $"/api/tasks/{t}/block", session: "s2");
        Assert.True(noReason == HttpStatusCode.BadRequest, noReasonBody.ToString());
        Assert.Equal("invalid_input", Error(noReasonBody));
        var (blocked, blockedBody) = await Send(HttpMethod.Post, $"/api/tasks/{t}/block", new { reason = "waiting" }, "s2");
        Assert.Equal(HttpStatusCode.OK, blocked);
        Assert.Equal("blocked", blockedBody.GetProperty("status").GetString());
        var (twice, twiceBody) = await Send(HttpMethod.Post, $"/api/tasks/{t}/block", new { reason = "again" }, "s2");
        Assert.Equal(HttpStatusCode.Conflict, twice);
        Assert.Equal("already_blocked", Error(twiceBody));

        // a blocked task can be taken; done by someone else needs takeover
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Post, $"/api/tasks/{t}/take", session: "s1")).Status);
        var (takeover, takeoverBody) = await Send(HttpMethod.Post, $"/api/tasks/{t}/done", new { summary = "closed", takeover = true }, "s2");
        Assert.Equal(HttpStatusCode.OK, takeover);
        Assert.Equal("s1", takeoverBody.GetProperty("note").GetProperty("takenOverFrom").GetString());

        // finished tasks: cancel is refused, done again is refused
        var (cancel, cancelBody) = await Send(HttpMethod.Post, $"/api/tasks/{t}/cancel", new { reason = "no" }, "s2");
        Assert.Equal(HttpStatusCode.Conflict, cancel);
        Assert.Equal("already_finished", Error(cancelBody));
    }

    [Fact]
    public async Task Cancel_and_update_work_for_the_lock_holder()
    {
        var project = await NewProject();
        var t = await AddTask(project, "T");
        var u = await AddTask(project, "U");

        var (updated, updatedBody) = await Send(HttpMethod.Patch, $"/api/tasks/{t}", new { title = "T2", priority = 3, scope = "" });
        Assert.Equal(HttpStatusCode.OK, updated);
        Assert.Equal("T2", updatedBody.GetProperty("title").GetString());
        Assert.Equal(3, updatedBody.GetProperty("priority").GetInt32());

        var (badPriority, badPriorityBody) = await Send(HttpMethod.Patch, $"/api/tasks/{t}", new { priority = 9 });
        Assert.Equal(HttpStatusCode.BadRequest, badPriority);
        Assert.Equal("invalid_input", Error(badPriorityBody));

        var (cancelled, cancelledBody) = await Send(HttpMethod.Post, $"/api/tasks/{u}/cancel", new { reason = "not needed" }, "s1");
        Assert.Equal(HttpStatusCode.OK, cancelled);
        Assert.Equal("cancelled", cancelledBody.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Malformed_input_is_400_with_a_reason()
    {
        var project = await NewProject();

        var (json, jsonBody) = await Send(HttpMethod.Post, $"/api/projects/{project}/tasks", rawBody: "{ not json", session: "s1");
        Assert.Equal(HttpStatusCode.BadRequest, json);
        Assert.Equal("bad_request", Error(jsonBody));

        var (priority, priorityBody) = await Send(HttpMethod.Post, $"/api/projects/{project}/tasks", new { title = "x", priority = 7 }, "s1");
        Assert.Equal(HttpStatusCode.BadRequest, priority);
        Assert.Equal("invalid_input", Error(priorityBody));

        var (title, titleBody) = await Send(HttpMethod.Post, $"/api/projects/{project}/tasks", new { scope = "no title" }, "s1");
        Assert.Equal(HttpStatusCode.BadRequest, title);
        Assert.Equal("invalid_input", Error(titleBody));

        var (noProject, noProjectBody) = await Send(HttpMethod.Post, "/api/projects/999/tasks", new { title = "x" }, "s1");
        Assert.Equal(HttpStatusCode.NotFound, noProject);
        Assert.Equal("not_found", Error(noProjectBody));

        // a non-numeric id never matches a route
        Assert.Equal(HttpStatusCode.NotFound, (await Send(HttpMethod.Get, "/api/tasks/abc")).Status);
    }

    // ------------------------------------------------------------------ decisions

    [Fact]
    public async Task Decisions_add_list_get_and_supersede()
    {
        var project = await NewProject();
        var task = await AddTask(project, "T");

        var (created, createdBody) = await Send(HttpMethod.Post, $"/api/projects/{project}/decisions",
            new { title = "Store", text = "sqlite", rationale = "single file", taskId = task }, "s1");
        Assert.Equal(HttpStatusCode.Created, created);
        var id = createdBody.GetProperty("id").GetInt64();

        var (_, list) = await Send(HttpMethod.Get, $"/api/projects/{project}/decisions");
        Assert.Equal(1, list.GetArrayLength());
        Assert.False(list[0].TryGetProperty("rationale", out _));
        Assert.Equal("active", list[0].GetProperty("status").GetString());

        var (_, one) = await Send(HttpMethod.Get, $"/api/decisions/{id}");
        Assert.Equal("single file", one.GetProperty("rationale").GetString());

        var (superseded, result) = await Send(HttpMethod.Post, $"/api/decisions/{id}/supersede",
            new { title = "Store v2", text = "postgres", rationale = "scale" }, "s1");
        Assert.Equal(HttpStatusCode.OK, superseded);
        var replacement = result.GetProperty("replacement").GetProperty("id").GetInt64();
        Assert.Equal("superseded", result.GetProperty("superseded").GetProperty("status").GetString());
        Assert.Equal(replacement, result.GetProperty("superseded").GetProperty("supersededById").GetInt64());

        var (_, active) = await Send(HttpMethod.Get, $"/api/projects/{project}/decisions?status=active");
        Assert.Equal(replacement, active.EnumerateArray().Single().GetProperty("id").GetInt64());
        var (_, all) = await Send(HttpMethod.Get, $"/api/projects/{project}/decisions");
        Assert.Equal(2, all.GetArrayLength());

        // next carries only the active one
        var (_, next) = await Send(HttpMethod.Post, $"/api/projects/{project}/tasks/next", session: "s1");
        Assert.Equal(replacement, next.GetProperty("decisions").EnumerateArray().Single().GetProperty("id").GetInt64());
    }

    [Fact]
    public async Task Decision_refusals()
    {
        var project = await NewProject();
        var other = await NewProject("other");
        var foreignTask = await AddTask(other, "elsewhere");

        var (tooLong, tooLongBody) = await Send(HttpMethod.Post, $"/api/projects/{project}/decisions",
            new { title = "T", text = new string('x', 441) }, "s1");
        Assert.Equal(HttpStatusCode.BadRequest, tooLong);
        Assert.Equal("invalid_input", Error(tooLongBody));
        Assert.Equal("ck_decision_text", tooLongBody.GetProperty("constraint").GetString());

        var (blank, blankBody) = await Send(HttpMethod.Post, $"/api/projects/{project}/decisions", new { title = "", text = "x" }, "s1");
        Assert.Equal(HttpStatusCode.BadRequest, blank);
        Assert.Equal("invalid_input", Error(blankBody));

        var (foreign, foreignBody) = await Send(HttpMethod.Post, $"/api/projects/{project}/decisions",
            new { title = "T", text = "x", taskId = foreignTask }, "s1");
        Assert.Equal(HttpStatusCode.BadRequest, foreign);
        Assert.Equal("invalid_input", Error(foreignBody));

        var (noProject, _) = await Send(HttpMethod.Post, "/api/projects/999/decisions", new { title = "T", text = "x" }, "s1");
        Assert.Equal(HttpStatusCode.NotFound, noProject);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(HttpMethod.Get, "/api/decisions/999")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(HttpMethod.Post, "/api/decisions/999/supersede", new { title = "T", text = "x" })).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(HttpMethod.Get, $"/api/projects/{project}/decisions?status=old")).Status);

        // superseding twice: 409, and the failed attempt leaves no replacement behind
        var (_, first) = await Send(HttpMethod.Post, $"/api/projects/{project}/decisions", new { title = "A", text = "a" }, "s1");
        var id = first.GetProperty("id").GetInt64();
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Post, $"/api/decisions/{id}/supersede", new { title = "B", text = "b" })).Status);
        var (twice, twiceBody) = await Send(HttpMethod.Post, $"/api/decisions/{id}/supersede", new { title = "C", text = "c" });
        Assert.Equal(HttpStatusCode.Conflict, twice);
        Assert.Equal("decision_already_superseded", Error(twiceBody));
        var (_, all) = await Send(HttpMethod.Get, $"/api/projects/{project}/decisions");
        Assert.Equal(2, all.GetArrayLength());
    }

    // ------------------------------------------------------------------ findings

    [Fact]
    public async Task Findings_add_list_without_body_get_with_body_and_filter()
    {
        var project = await NewProject();
        var task = await AddTask(project, "T");
        var (_, decision) = await Send(HttpMethod.Post, $"/api/projects/{project}/decisions", new { title = "D", text = "x" }, "s1");
        var decisionId = decision.GetProperty("id").GetInt64();

        var (created, createdBody) = await Send(HttpMethod.Post, $"/api/projects/{project}/findings",
            new { title = "Distribution", body = "| a | b |\n|---|---|", taskId = task, decisionId }, "s1");
        Assert.Equal(HttpStatusCode.Created, created);
        var id = createdBody.GetProperty("id").GetInt64();
        Assert.Equal("s1", createdBody.GetProperty("sessionId").GetString());
        await Send(HttpMethod.Post, $"/api/projects/{project}/findings", new { title = "Loose", body = "no traces" });

        var (_, list) = await Send(HttpMethod.Get, $"/api/projects/{project}/findings");
        Assert.Equal(2, list.GetArrayLength());
        Assert.Equal("Loose", list[0].GetProperty("title").GetString()); // newest first
        Assert.False(list[0].TryGetProperty("body", out _));
        Assert.Equal(19, list[1].GetProperty("bodyLength").GetInt32());

        var (_, byTask) = await Send(HttpMethod.Get, $"/api/projects/{project}/findings?taskId={task}");
        Assert.Equal(id, byTask.EnumerateArray().Single().GetProperty("id").GetInt64());
        var (_, byDecision) = await Send(HttpMethod.Get, $"/api/projects/{project}/findings?decisionId={decisionId}");
        Assert.Equal(id, byDecision.EnumerateArray().Single().GetProperty("id").GetInt64());

        var (_, one) = await Send(HttpMethod.Get, $"/api/findings/{id}");
        Assert.Equal("| a | b |\n|---|---|", one.GetProperty("body").GetString());

        // findings never reach the next response
        var (_, next) = await Send(HttpMethod.Post, $"/api/projects/{project}/tasks/next", session: "s1");
        Assert.False(next.TryGetProperty("findings", out _));
    }

    [Fact]
    public async Task Finding_refusals()
    {
        var project = await NewProject();
        var other = await NewProject("other");
        var foreignTask = await AddTask(other, "elsewhere");
        var (_, foreignDecision) = await Send(HttpMethod.Post, $"/api/projects/{other}/decisions", new { title = "D", text = "x" }, "s1");

        var (tooLong, tooLongBody) = await Send(HttpMethod.Post, $"/api/projects/{project}/findings", new { title = "T", body = new string('x', 11001) });
        Assert.Equal(HttpStatusCode.BadRequest, tooLong);
        Assert.Equal("invalid_input", Error(tooLongBody));
        Assert.Equal("ck_finding_body", tooLongBody.GetProperty("constraint").GetString());

        var (longTitle, longTitleBody) = await Send(HttpMethod.Post, $"/api/projects/{project}/findings", new { title = new string('t', 201), body = "b" });
        Assert.Equal(HttpStatusCode.BadRequest, longTitle);
        Assert.Equal("ck_finding_title", longTitleBody.GetProperty("constraint").GetString());

        Assert.Equal(HttpStatusCode.BadRequest, (await Send(HttpMethod.Post, $"/api/projects/{project}/findings", new { title = "", body = "b" })).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(HttpMethod.Post, $"/api/projects/{project}/findings", new { title = "T", body = "  " })).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(HttpMethod.Post, $"/api/projects/{project}/findings", new { title = "T", body = "b", taskId = foreignTask })).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(HttpMethod.Post, $"/api/projects/{project}/findings",
            new { title = "T", body = "b", decisionId = foreignDecision.GetProperty("id").GetInt64() })).Status);

        Assert.Equal(HttpStatusCode.NotFound, (await Send(HttpMethod.Post, "/api/projects/999/findings", new { title = "T", body = "b" })).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(HttpMethod.Get, "/api/projects/999/findings")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(HttpMethod.Get, "/api/findings/999")).Status);

        var (_, all) = await Send(HttpMethod.Get, $"/api/projects/{project}/findings");
        Assert.Equal(0, all.GetArrayLength());
    }
}
