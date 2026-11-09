using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Deming.Data;
using Deming.Server;
using Deming.Server.Api;
using Microsoft.AspNetCore.Builder;

namespace Deming.Tests;

/// <summary>The message endpoints over real HTTP (Kestrel on a free loopback port, temporary database, generated key).</summary>
public sealed class MessageApiTests : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _path = TempDatabase.NewPath();
    private readonly string _key = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
    private WebApplication _app = null!;
    private HttpClient _http = null!;

    public async ValueTask InitializeAsync()
    {
        _app = DemingServer.Build(["--urls=https://127.0.0.1:0"], b =>
        {
            b.Configuration[DataServiceCollectionExtensions.DatabasePathKey] = _path;
            b.Configuration[ApiKeySettings.ConfigKey] = _key;
        }, TestCertificate.Source);
        await _app.StartAsync(Ct);
        _http = TestCertificate.Client(new Uri(_app.Urls.First()));
        _http.DefaultRequestHeaders.Add(ApiKeySettings.HeaderName, _key);
    }

    public async ValueTask DisposeAsync()
    {
        _http.Dispose();
        await _app.DisposeAsync();
        TempDatabase.DeleteFiles(_path, new DemingDb(_path).ConnectionString);
    }

    private async Task<(HttpStatusCode Status, JsonElement Body)> Send(HttpMethod method, string url, object? body = null, string? session = null)
    {
        using var request = new HttpRequestMessage(method, url);
        if (body is not null)
            request.Content = JsonContent.Create(body);
        if (session is not null)
            request.Headers.Add(ApiKeySettings.SessionHeaderName, session);
        using var response = await _http.SendAsync(request, Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        return (response.StatusCode, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone());
    }

    private static string Error(JsonElement body) => body.GetProperty("error").GetString()!;

    private async Task<long> NewProject(string name)
    {
        var (status, body) = await Send(HttpMethod.Post, "/api/projects", new { name });
        Assert.Equal(HttpStatusCode.Created, status);
        return body.GetProperty("id").GetInt64();
    }

    private async Task<long> AddTask(long project, string title)
    {
        var (status, body) = await Send(HttpMethod.Post, $"/api/projects/{project}/tasks", new { title }, "s1");
        Assert.Equal(HttpStatusCode.Created, status);
        return body.GetProperty("id").GetInt64();
    }

    private Task<(HttpStatusCode Status, JsonElement Body)> Post(object message, string? session = "s1") =>
        Send(HttpMethod.Post, "/api/messages", message, session);

    [Fact]
    public async Task Post_answers_201_without_the_body_and_get_returns_it()
    {
        var project = await NewProject("demo");

        var (status, posted) = await Post(new { project, sender = "Claude", to = "codex", body = "Which branch?" });

        Assert.Equal(HttpStatusCode.Created, status);
        Assert.Equal("claude", posted.GetProperty("sender").GetString());
        Assert.Equal("codex", posted.GetProperty("recipient").GetString());
        Assert.Equal("s1", posted.GetProperty("senderSessionId").GetString());
        Assert.False(posted.TryGetProperty("body", out _));

        var id = posted.GetProperty("id").GetInt64();
        var (getStatus, message) = await Send(HttpMethod.Get, $"/api/messages/{id}");
        Assert.Equal(HttpStatusCode.OK, getStatus);
        Assert.Equal("Which branch?", message.GetProperty("body").GetString());
        Assert.Equal(JsonValueKind.Null, message.GetProperty("taskId").ValueKind);

        Assert.Equal(HttpStatusCode.NotFound, (await Send(HttpMethod.Get, "/api/messages/999")).Status);
    }

    [Fact]
    public async Task Write_refusals_are_404_400_and_409()
    {
        var project = await NewProject("demo");
        var other = await NewProject("other");
        var foreign = await AddTask(other, "elsewhere");
        var task = await AddTask(project, "here");
        var question = (await Post(new { project, taskId = task, sender = "claude", body = "q?" })).Body.GetProperty("id").GetInt64();

        var (s1, b1) = await Post(new { project = 999, sender = "claude", body = "x" });
        Assert.Equal(HttpStatusCode.NotFound, s1);
        Assert.Equal("not_found", Error(b1));

        var (s2, b2) = await Post(new { project, taskId = foreign, sender = "claude", body = "x" });
        Assert.Equal(HttpStatusCode.BadRequest, s2);
        Assert.Equal("message_task_other_project", Error(b2));

        var (s3, b3) = await Post(new { project, sender = "claude", replyToId = question, body = "x" });
        Assert.Equal(HttpStatusCode.BadRequest, s3);
        Assert.Equal("message_reply_other_channel", Error(b3));

        var (s4, b4) = await Post(new { project, sender = "claude", body = new string('x', 2201) });
        Assert.Equal(HttpStatusCode.BadRequest, s4);
        Assert.Equal("ck_message_body", b4.GetProperty("constraint").GetString());

        var (s5, b5) = await Post(new { project, sender = "çalışan", body = "x" });
        Assert.Equal(HttpStatusCode.BadRequest, s5);
        Assert.Equal("ck_message_sender", b5.GetProperty("constraint").GetString());

        var (s6, _) = await Post(new { project, body = "no sender" });
        Assert.Equal(HttpStatusCode.BadRequest, s6);

        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Post, $"/api/tasks/{task}/take", session: "s1")).Status);
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Post, $"/api/tasks/{task}/done", new { summary = "ok" }, "s1")).Status);
        var (s7, b7) = await Post(new { project, taskId = task, sender = "claude", body = "too late" });
        Assert.Equal(HttpStatusCode.Conflict, s7);
        Assert.Equal("message_task_finished", Error(b7));
    }

    [Fact]
    public async Task Read_refusals_are_400_and_404()
    {
        var project = await NewProject("demo");
        var task = await AddTask(project, "here");

        foreach (var query in new[]
                 {
                     "", $"project={project}&wait=61", $"project={project}&wait=-1", $"project={project}&top=0",
                     $"project={project}&top=101", $"project={project}&task={task}&all=true", $"project={project}&wait=soon",
                 })
        {
            var (status, _) = await Send(HttpMethod.Get, "/api/messages?" + query);
            Assert.True(status == HttpStatusCode.BadRequest, $"{query}: {status}");
        }

        Assert.Equal(HttpStatusCode.NotFound, (await Send(HttpMethod.Get, "/api/messages?project=999")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(HttpMethod.Get, $"/api/messages?project={project}&task=999")).Status);
    }

    [Fact]
    public async Task Long_poll_answers_at_once_when_there_is_a_message_waits_for_the_next_one_and_ends_empty()
    {
        var project = await NewProject("demo");
        var first = (await Post(new { project, sender = "claude", body = "one" })).Body.GetProperty("id").GetInt64();

        var clock = Stopwatch.StartNew();
        var (status, page) = await Send(HttpMethod.Get, $"/api/messages?project={project}&wait=30");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(first, page.GetProperty("lastMessageId").GetInt64());
        Assert.False(page.GetProperty("hasMore").GetBoolean());
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5));

        var waiting = Send(HttpMethod.Get, $"/api/messages?project={project}&after={first}&wait=30&to=codex");
        await Task.Delay(300, Ct);
        Assert.False(waiting.IsCompleted);
        var second = (await Post(new { project, sender = "claude", to = "codex", body = "two" })).Body.GetProperty("id").GetInt64();
        var (_, woken) = await waiting;
        Assert.Equal(second, woken.GetProperty("messages")[0].GetProperty("id").GetInt64());
        Assert.Equal(second, woken.GetProperty("lastMessageId").GetInt64());

        clock.Restart();
        var (_, empty) = await Send(HttpMethod.Get, $"/api/messages?project={project}&after={second}&wait=1");
        Assert.Equal(0, empty.GetProperty("messages").GetArrayLength());
        Assert.Equal(second, empty.GetProperty("lastMessageId").GetInt64());
        Assert.True(clock.Elapsed >= TimeSpan.FromSeconds(0.9));
    }

    [Fact]
    public async Task Done_carries_the_channel_counts_and_next_carries_no_messages()
    {
        var project = await NewProject("demo");
        var task = await AddTask(project, "here");
        await Post(new { project, taskId = task, sender = "codex", to = "claude", body = "do not forget the README" });

        var (_, next) = await Send(HttpMethod.Post, $"/api/projects/{project}/tasks/next", session: "s1");
        Assert.DoesNotContain("README", next.GetRawText(), StringComparison.Ordinal);

        var (status, done) = await Send(HttpMethod.Post, $"/api/tasks/{task}/done", new { summary = "ok" }, "s1");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(1, done.GetProperty("messageCount").GetInt64());
        Assert.Equal(1, done.GetProperty("unansweredQuestionCount").GetInt64());
    }
}
