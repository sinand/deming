using System.Security.Cryptography;
using System.Text.Json;
using Deming.Cli;
using Deming.Data;
using Deming.Server;
using Deming.Server.Api;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace Deming.Tests;

/// <summary>The CLI against a real server (Kestrel on a free loopback port, temporary database, generated key and config path).</summary>
public sealed class CliClientTests : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _dbPath = TempDatabase.NewPath();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "deming-tests", Guid.NewGuid().ToString("N"));
    private readonly string _key = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
    private readonly SocketsHttpHandler _handler = TestCertificate.Handler();
    private WebApplication _app = null!;
    private Dictionary<string, string> _env = null!;

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_dir);
        _app = DemingServer.Build(["--urls=https://127.0.0.1:0"], b =>
        {
            b.Configuration[DataServiceCollectionExtensions.DatabasePathKey] = _dbPath;
            b.Configuration[ApiKeySettings.ConfigKey] = _key;
        }, TestCertificate.Source);
        await _app.StartAsync(Ct);
        _env = new Dictionary<string, string>
        {
            ["DEMING_URL"] = _app.Urls.First(),
            ["DEMING_API_KEY"] = _key,
            ["DEMING_SESSION_ID"] = "session-a",
            ["DEMING_CONFIG"] = Path.Combine(_dir, "config.json"), // never the real user directory
        };
    }

    public async ValueTask DisposeAsync()
    {
        _handler.Dispose();
        await _app.DisposeAsync();
        TempDatabase.DeleteFiles(_dbPath, new DemingDb(_dbPath).ConnectionString);
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private async Task<(int Code, string Out, string Err)> Cli(params string[] args) => await CliAs(_env, args);

    private async Task<(int Code, string Out, string Err)> CliAs(Dictionary<string, string> env, params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var code = await CliApp.RunAsync(args, new CliContext
        {
            Out = stdout,
            Err = stderr,
            Env = name => env.GetValueOrDefault(name),
            CurrentDirectory = _dir,
            HttpHandler = _handler,
        });
        return (code, stdout.ToString().Trim(), stderr.ToString().Trim());
    }

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    private async Task Init() => Assert.Equal(0, (await Cli("init", "demo")).Code);

    [Fact]
    public async Task Init_writes_the_dotdeming_file_and_is_repeatable()
    {
        var (code, output, _) = await Cli("init", "demo");

        Assert.Equal(0, code);
        Assert.True(Json(output).GetProperty("created").GetBoolean());
        Assert.Equal("demo", File.ReadAllText(Path.Combine(_dir, ".deming")).Trim());

        var again = await Cli("init", "demo");
        Assert.Equal(0, again.Code);
        Assert.False(Json(again.Out).GetProperty("created").GetBoolean());

        var other = await Cli("init", "other");
        Assert.Equal(ExitCodes.Error, other.Code);
        Assert.Contains("already names project 'demo'", other.Err);
    }

    [Fact]
    public async Task Init_sends_the_repo_path_and_refreshes_it_on_an_existing_project()
    {
        var projects = _app.Services.GetRequiredService<Deming.Data.Services.ProjectService>();

        var first = await Cli("init", "demo");
        Assert.Equal(_dir, Json(first.Out).GetProperty("repoPath").GetString());
        var project = (await projects.GetByNameAsync("demo", Ct))!;
        Assert.Equal(_dir, project.RepoPath);

        await projects.UpdateAsync(project.Id, new Deming.Data.Services.ProjectChanges(RepoPath: "D:\\moved"), Ct);
        Assert.Equal(0, (await Cli("init", "demo")).Code);
        Assert.Equal(_dir, (await projects.GetByIdAsync(project.Id, Ct))!.RepoPath);
    }

    [Fact]
    public async Task An_inactive_project_makes_next_and_add_exit_1()
    {
        await Init();
        var projects = _app.Services.GetRequiredService<Deming.Data.Services.ProjectService>();
        var id = (await projects.GetByNameAsync("demo", Ct))!.Id;
        await projects.UpdateAsync(id, new Deming.Data.Services.ProjectChanges(IsActive: false), Ct);

        var next = await Cli("next");
        Assert.Equal(ExitCodes.Error, next.Code);
        Assert.Contains("project_inactive", next.Err);
        Assert.Equal(ExitCodes.Error, (await Cli("add", """{"title":"x"}""")).Code);
    }

    [Fact]
    public async Task Project_comes_from_the_file_not_the_directory()
    {
        var (code, _, err) = await Cli("list");

        Assert.Equal(ExitCodes.Error, code);
        Assert.Contains("No .deming file", err);
    }

    [Fact]
    public async Task Parallel_lists_the_eligible_tasks_and_exits_3_when_none()
    {
        await Init();

        var empty = await Cli("parallel");
        Assert.Equal(ExitCodes.NothingToDo, empty.Code);
        Assert.Equal("[]", empty.Out);

        var first = Json((await Cli("add", "--title", "First")).Out).GetProperty("id").GetInt64();
        await Cli("add", "--title", "Second", "--depends-on", first.ToString());
        var third = Json((await Cli("add", "--title", "Third")).Out).GetProperty("id").GetInt64();

        var (code, output, _) = await Cli("parallel");
        Assert.Equal(0, code);
        Assert.Equal([first, third], Json(output).EnumerateArray().Select(r => r.GetProperty("id").GetInt64()));
    }

    [Fact]
    public async Task Add_next_done_round_trip_with_exit_codes()
    {
        await Init();

        var empty = await Cli("next");
        Assert.Equal(ExitCodes.NothingToDo, empty.Code);

        var added = await Cli("add", """{"title":"First","scope":"do it"}""");
        Assert.Equal(0, added.Code);
        var id = Json(added.Out).GetProperty("id").GetInt64();

        var duplicate = await Cli("create", """{"title":"first"}""");
        Assert.Equal(ExitCodes.AlreadyExists, duplicate.Code);
        Assert.Contains($"already exists: task {id}", duplicate.Err);

        var next = await Cli("next");
        Assert.Equal(0, next.Code);
        Assert.Equal(id, Json(next.Out).GetProperty("task").GetProperty("id").GetInt64());

        var done = await Cli("done", id.ToString(), """{"summary":"finished","commitHash":"abc"}""");
        Assert.Equal(0, done.Code);

        var list = await Cli("list");
        Assert.Equal("done", Json(list.Out)[0].GetProperty("status").GetString());

        var show = await Cli("show", id.ToString());
        Assert.Equal(1, Json(show.Out).GetProperty("notes").GetArrayLength());
    }

    [Fact]
    public async Task Takeover_flag_closes_a_task_held_by_another_session()
    {
        await Init();
        var id = Json((await Cli("add", """{"title":"Held"}""")).Out).GetProperty("id").GetInt64().ToString();
        Assert.Equal(0, (await Cli("take", id)).Code);

        var other = new Dictionary<string, string>(_env) { ["DEMING_SESSION_ID"] = "session-b" };
        var refused = await CliAs(other, "done", id, """{"summary":"x"}""");
        Assert.Equal(ExitCodes.Error, refused.Code);
        Assert.Contains("locked_by_other_session", refused.Err);

        var ok = await CliAs(other, "done", id, """{"summary":"x"}""", "--takeover");
        Assert.Equal(0, ok.Code);
    }

    [Fact]
    public async Task Block_cancel_and_requeue_take_plain_reasons()
    {
        await Init();
        var a = Json((await Cli("add", """{"title":"A"}""")).Out).GetProperty("id").GetInt64().ToString();
        var b = Json((await Cli("add", """{"title":"B"}""")).Out).GetProperty("id").GetInt64().ToString();

        Assert.Equal(0, (await Cli("take", a)).Code);
        Assert.Equal(0, (await Cli("block", a, "waiting", "for", "input")).Code);
        Assert.Contains("waiting for input", (await Cli("show", a)).Out);
        Assert.Equal(0, (await Cli("requeue", a)).Code);

        Assert.Equal(0, (await Cli("cancel", b, """{"reason":"obsolete"}""")).Code);
        Assert.Equal("cancelled", Json((await Cli("list", "--status", "cancelled")).Out)[0].GetProperty("status").GetString());
    }

    [Fact]
    public async Task Decisions_are_recorded_listed_and_superseded()
    {
        await Init();
        var first = Json((await Cli("decide", """{"title":"T","text":"one","rationale":"because"}""")).Out).GetProperty("id").GetInt64();

        var replaced = await Cli("decide", """{"title":"T2","text":"two"}""", "--supersede", first.ToString());
        Assert.Equal(0, replaced.Code);

        var active = Json((await Cli("decisions", "--status", "active")).Out);
        Assert.Equal(1, active.GetArrayLength());
        Assert.Equal("T2", active[0].GetProperty("title").GetString());

        var one = Json((await Cli("decisions", first.ToString())).Out);
        Assert.Equal("because", one.GetProperty("rationale").GetString());

        Assert.Contains("demo", (await Cli("projects")).Out);
    }

    [Fact]
    public async Task Findings_are_recorded_listed_filtered_and_read_with_their_body()
    {
        await Init();
        var task = Json((await Cli("add", """{"title":"T"}""")).Out).GetProperty("id").GetInt64();
        var first = Json((await Cli("finding", $$"""{"title":"Inline","body":"# one","taskId":{{task}}}""")).Out).GetProperty("id").GetInt64();

        var file = Path.Combine(_dir, "body.md");
        await File.WriteAllTextAsync(file, "çok **uzun**\n\n| a |\n|---|\n", System.Text.Encoding.UTF8, Ct);
        var second = Json((await Cli("finding", """{"title":"From file"}""", "--body-file", file)).Out).GetProperty("id").GetInt64();

        var list = Json((await Cli("findings")).Out);
        Assert.Equal(2, list.GetArrayLength());
        Assert.Equal(second, list[0].GetProperty("id").GetInt64());
        Assert.False(list[0].TryGetProperty("body", out _));

        var byTask = Json((await Cli("findings", "--task", task.ToString())).Out);
        Assert.Equal(first, byTask.EnumerateArray().Single().GetProperty("id").GetInt64());

        Assert.Equal("çok **uzun**\n\n| a |\n|---|\n", Json((await Cli("findings", second.ToString())).Out).GetProperty("body").GetString());

        var missing = await Cli("finding", """{"title":"X"}""", "--body-file", Path.Combine(_dir, "nope.md"));
        Assert.Equal(ExitCodes.Error, missing.Code);
        Assert.Equal(ExitCodes.Error, (await Cli("finding", """{"title":"No body"}""")).Code);
        Assert.Equal(ExitCodes.Error, (await Cli("findings", "--task", "x")).Code);
    }

    [Theory]
    [InlineData("next")]
    [InlineData("requeue", "1")]
    public async Task Session_commands_stop_without_a_session_id(params string[] args)
    {
        await Init();
        var env = new Dictionary<string, string>(_env);
        env.Remove("DEMING_SESSION_ID");

        var (code, _, err) = await CliAs(env, args);

        Assert.Equal(ExitCodes.Error, code);
        Assert.Contains("No session id", err);
    }

    [Fact]
    public async Task Claude_code_session_id_is_the_fallback()
    {
        await Init();
        var env = new Dictionary<string, string>(_env) { ["CLAUDE_CODE_SESSION_ID"] = "cc-1" };
        env.Remove("DEMING_SESSION_ID");
        await Cli("add", """{"title":"X"}""");

        Assert.Equal(0, (await CliAs(env, "next")).Code);
    }

    [Fact]
    public async Task Missing_arguments_list_the_json_fields()
    {
        await Init();

        var add = await Cli("add");
        Assert.Equal(ExitCodes.Error, add.Code);
        Assert.Contains("Missing", add.Err);
        Assert.Contains("dependsOn", add.Err);

        var done = await Cli("done");
        Assert.Contains("Missing task id", done.Err);
        Assert.Contains("touchedFiles", done.Err);

        var help = await Cli("help", "done");
        Assert.Equal(0, help.Code);
        Assert.Contains("summary", help.Out);
    }

    [Fact]
    public async Task Fields_can_be_given_as_flags_with_numbers_and_id_lists_converted()
    {
        await Init();
        var dep1 = Json((await Cli("add", "--title", "Dep one")).Out).GetProperty("id").GetInt64();
        var dep2 = Json((await Cli("add", "--title=Dep two")).Out).GetProperty("id").GetInt64();

        var added = await Cli("add", "--title", "Write the first test", "--scope", "say \"hi\"", "--priority", "1", "--depends-on", $"{dep1},{dep2}");
        Assert.Equal(0, added.Code);
        var id = Json(added.Out).GetProperty("id").GetInt64().ToString();

        var show = Json((await Cli("show", id)).Out);
        var task = show.GetProperty("task");
        Assert.Equal("Write the first test", task.GetProperty("title").GetString());
        Assert.Equal("say \"hi\"", task.GetProperty("scope").GetString());
        Assert.Equal(1, task.GetProperty("priority").GetInt32());
        Assert.Contains("Dep one", show.GetRawText());
        Assert.Contains("Dep two", show.GetRawText());

        var updated = await Cli("update", id, "--priority", "3", "--scope", "", "--depends-on", "");
        Assert.Equal(0, updated.Code);
        var after = Json((await Cli("show", id)).Out).GetProperty("task");
        Assert.Equal(3, after.GetProperty("priority").GetInt32());

        var duplicate = await Cli("add", "--title", "write the first test");
        Assert.Equal(ExitCodes.AlreadyExists, duplicate.Code);
    }

    [Fact]
    public async Task Flags_work_for_done_block_cancel_decide_and_finding()
    {
        await Init();
        var a = Json((await Cli("add", "--title", "A")).Out).GetProperty("id").GetInt64().ToString();
        var b = Json((await Cli("add", "--title", "B")).Out).GetProperty("id").GetInt64().ToString();
        var c = Json((await Cli("add", "--title", "C")).Out).GetProperty("id").GetInt64().ToString();

        Assert.Equal(0, (await Cli("take", a)).Code);
        Assert.Equal(0, (await Cli("done", a, "--summary", "all good", "--caveats", "none", "--touched-files", "a.cs", "--commit-hash", "abc")).Code);
        var note = Json((await Cli("show", a)).Out).GetProperty("notes")[0];
        Assert.Equal("all good", note.GetProperty("summary").GetString());
        Assert.Equal("a.cs", note.GetProperty("touchedFiles").GetString());
        Assert.Equal("abc", note.GetProperty("commitHash").GetString());

        Assert.Equal(0, (await Cli("take", b)).Code);
        Assert.Equal(0, (await Cli("block", b, "--reason", "needs input")).Code);
        Assert.Contains("needs input", (await Cli("show", b)).Out);
        Assert.Equal(0, (await Cli("cancel", c, "--reason", "obsolete")).Code);

        var first = Json((await Cli("decide", "--title", "T", "--text", "one", "--rationale", "because", "--task-id", a)).Out).GetProperty("id").GetInt64();
        Assert.Equal(0, (await Cli("decide", "--title", "T2", "--text", "two", "--supersede", first.ToString())).Code);
        Assert.Equal(a, Json((await Cli("decisions", first.ToString())).Out).GetProperty("taskId").GetInt64().ToString());

        var finding = await Cli("finding", "--title", "Measured", "--body", "# 12 ms", "--task-id", a, "--decision-id", first.ToString());
        Assert.Equal(0, finding.Code);
        var findingId = Json(finding.Out).GetProperty("id").GetInt64().ToString();
        Assert.Equal("# 12 ms", Json((await Cli("findings", findingId)).Out).GetProperty("body").GetString());

        var file = Path.Combine(_dir, "flag-body.md");
        await File.WriteAllTextAsync(file, "from a file", System.Text.Encoding.UTF8, Ct);
        var fromFile = await Cli("finding", "--title", "Filed", "--body-file", file);
        Assert.Equal(0, fromFile.Code);
        Assert.Equal(ExitCodes.Error, (await Cli("finding", "--title", "Both", "--body", "x", "--body-file", file)).Code);
    }

    [Fact]
    public async Task Takeover_switch_works_with_flags_and_with_a_json_argument()
    {
        await Init();
        var id = Json((await Cli("add", "--title", "Held")).Out).GetProperty("id").GetInt64().ToString();
        Assert.Equal(0, (await Cli("take", id)).Code);
        var other = new Dictionary<string, string>(_env) { ["DEMING_SESSION_ID"] = "session-b" };

        Assert.Equal(ExitCodes.Error, (await CliAs(other, "done", id, "--summary", "x")).Code);
        Assert.Equal(0, (await CliAs(other, "done", id, "--summary", "x", "--takeover")).Code);
    }

    [Fact]
    public async Task Flags_and_a_json_argument_together_are_an_error_and_nothing_is_sent()
    {
        await Init();

        var both = await Cli("add", """{"title":"From json"}""", "--scope", "from flag");
        Assert.Equal(ExitCodes.Error, both.Code);
        Assert.Contains("not both", both.Err);
        Assert.Contains("--scope", both.Err);
        Assert.Equal(0, Json((await Cli("list")).Out).GetArrayLength());

        var stdin = await Cli("add", "-", "--title", "x");
        Assert.Equal(ExitCodes.Error, stdin.Code);

        var reason = await Cli("block", "1", "words", "--reason", "flag");
        Assert.Equal(ExitCodes.Error, reason.Code);
        Assert.Contains("not both", reason.Err);

        // takeover in the JSON and as a flag: given twice
        var twice = await Cli("done", "1", """{"summary":"x","takeover":true}""", "--takeover");
        Assert.Equal(ExitCodes.Error, twice.Code);
    }

    [Fact]
    public async Task A_bad_flag_value_names_the_field()
    {
        await Init();

        var priority = await Cli("add", "--title", "X", "--priority", "high");
        Assert.Equal(ExitCodes.Error, priority.Code);
        Assert.Contains("--priority", priority.Err);
        Assert.Contains("'high'", priority.Err);

        var ids = await Cli("add", "--title", "X", "--depends-on", "3,abc");
        Assert.Equal(ExitCodes.Error, ids.Code);
        Assert.Contains("--depends-on", ids.Err);
        Assert.Contains("'abc'", ids.Err);

        var dangling = await Cli("add", "--title");
        Assert.Equal(ExitCodes.Error, dangling.Code);
        Assert.Contains("--title needs a value", dangling.Err);

        var badSwitch = await Cli("done", "1", "--summary", "x", "--takeover=maybe");
        Assert.Equal(ExitCodes.Error, badSwitch.Code);
        Assert.Contains("--takeover", badSwitch.Err);
        Assert.Equal(0, Json((await Cli("list")).Out).GetArrayLength());
    }

    [Fact]
    public async Task Help_and_missing_arguments_show_the_flag_form_of_every_field()
    {
        await Init();

        var add = await Cli("help", "add");
        Assert.Contains("--title <text>", add.Out);
        Assert.Contains("--depends-on <ids>", add.Out);
        Assert.Contains("(JSON: dependsOn)", add.Out);

        var done = await Cli("done", "--help");
        Assert.Contains("--touched-files <text>", done.Out);
        Assert.Contains("--commit-hash <text>", done.Out);
        Assert.Contains("--takeover", done.Out);

        var missing = await Cli("add");
        Assert.Equal(ExitCodes.Error, missing.Code);
        Assert.Contains("--depends-on <ids>", missing.Err);

        var badJson = await Cli("add", "{title:oops}");
        Assert.Contains("not valid JSON", badJson.Err);
        Assert.Contains("as flags", badJson.Err);
    }

    [Fact]
    public async Task Wrong_key_and_unreachable_server_are_errors_with_hints()
    {
        var wrong = new Dictionary<string, string>(_env) { ["DEMING_API_KEY"] = "nope" };
        var denied = await CliAs(wrong, "projects");
        Assert.Equal(ExitCodes.Error, denied.Code);
        Assert.Contains("invalid_api_key", denied.Err);

        var gone = new Dictionary<string, string>(_env) { ["DEMING_URL"] = "https://127.0.0.1:1" };
        var unreachable = await CliAs(gone, "projects");
        Assert.Equal(ExitCodes.Error, unreachable.Code);
        Assert.Contains("Cannot reach the server", unreachable.Err);
        Assert.DoesNotContain(_key, unreachable.Err);
    }

    [Fact]
    public async Task Plain_http_url_is_refused_from_the_environment_and_from_the_config_file()
    {
        var http = new Dictionary<string, string>(_env) { ["DEMING_URL"] = "http://127.0.0.1:5000" };
        var fromEnv = await CliAs(http, "projects");
        Assert.Equal(ExitCodes.Error, fromEnv.Code);
        Assert.Contains("http is not allowed, use https", fromEnv.Err);

        File.WriteAllText(_env["DEMING_CONFIG"], """{"url":"http://127.0.0.1:5000"}""");
        var env = new Dictionary<string, string>(_env);
        env.Remove("DEMING_URL");
        var fromFile = await CliAs(env, "projects");
        Assert.Equal(ExitCodes.Error, fromFile.Code);
        Assert.Contains("http is not allowed, use https", fromFile.Err);
    }

    [Fact]
    public void Default_address_is_https_on_5001() => Assert.Equal("https://127.0.0.1:5001", CliRun.DefaultUrl);

    /// <summary>The CLI with its own HTTP client (no test handler): only the system and the config's fingerprint decide.</summary>
    private async Task<(int Code, string Out, string Err)> CliOwnClient(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var code = await CliApp.RunAsync(args, new CliContext
        {
            Out = stdout,
            Err = stderr,
            Env = name => _env.GetValueOrDefault(name),
            CurrentDirectory = _dir,
        });
        return (code, stdout.ToString().Trim(), stderr.ToString().Trim());
    }

    [Fact]
    public async Task Without_a_recorded_fingerprint_the_servers_certificate_is_not_trusted()
    {
        var (code, _, err) = await CliOwnClient("projects");

        Assert.Equal(ExitCodes.Error, code);
        Assert.Contains("not trusted", err);
        Assert.Contains(UserConfig.CertificateKey, err);
        Assert.DoesNotContain("dev-certs", err);
    }

    [Fact]
    public async Task The_recorded_fingerprint_is_trusted_and_nothing_else()
    {
        File.WriteAllText(_env["DEMING_CONFIG"], $$"""{"certificateSha256":"{{TestCertificate.Instance.GetCertHashString(HashAlgorithmName.SHA256)}}"}""");
        var (code, output, err) = await CliOwnClient("projects");
        Assert.True(code == 0, err);
        Assert.Equal("[]", output);

        // Colon-separated lower-case hex is the same fingerprint.
        var colons = string.Join(':', Convert.ToHexString(TestCertificate.Instance.GetCertHash(HashAlgorithmName.SHA256)).ToLowerInvariant().Chunk(2).Select(c => new string(c)));
        File.WriteAllText(_env["DEMING_CONFIG"], $$"""{"certificateSha256":"{{colons}}"}""");
        Assert.Equal(0, (await CliOwnClient("projects")).Code);

        // Another certificate's fingerprint: the server is not trusted.
        using var other = Deming.Server.Exposure.ServerCertificate.Create(DateTimeOffset.UtcNow);
        File.WriteAllText(_env["DEMING_CONFIG"], $$"""{"certificateSha256":"{{other.GetCertHashString(HashAlgorithmName.SHA256)}}"}""");
        var refused = await CliOwnClient("projects");
        Assert.Equal(ExitCodes.Error, refused.Code);
        Assert.Contains("not trusted", refused.Err);
    }

    [Fact]
    public async Task A_malformed_fingerprint_is_an_error_before_anything_is_sent()
    {
        File.WriteAllText(_env["DEMING_CONFIG"], """{"certificateSha256":"abc"}""");

        var (code, _, err) = await CliOwnClient("projects");

        Assert.Equal(ExitCodes.Error, code);
        Assert.Contains("is not a SHA-256 fingerprint", err);
    }

    [Fact]
    public async Task Key_and_url_fall_back_to_the_config_file()
    {
        File.WriteAllText(_env["DEMING_CONFIG"], $$"""{"apiKey":"{{_key}}","url":"{{_env["DEMING_URL"]}}","extra":1}""");
        var env = new Dictionary<string, string>(_env);
        env.Remove("DEMING_API_KEY");
        env.Remove("DEMING_URL");

        var (code, output, _) = await CliAs(env, "projects");

        Assert.Equal(0, code);
        Assert.Equal("[]", output);
    }

    [Fact]
    public async Task An_unknown_flag_is_refused_by_name_with_the_usage_and_nothing_is_sent()
    {
        await Init();

        var typo = await Cli("add", "--titel", "Typo");
        Assert.Equal(ExitCodes.Error, typo.Code);
        Assert.Contains("unknown flag --titel", typo.Err);
        Assert.Contains("Usage: deming add", typo.Err);
        Assert.DoesNotContain("JSON", typo.Err.Split('\n')[0]);

        // A valueless typo is no longer ignored, and the --name=value form is checked too.
        var takeover = await Cli("done", "1", "--takover");
        Assert.Contains("unknown flag --takover", takeover.Err);
        Assert.Contains("unknown flag --titel", (await Cli("add", "--titel=Typo")).Err);

        // A flag that belongs to another command is unknown here.
        Assert.Contains("unknown flag --force", (await Cli("show", "1", "--force")).Err);
        Assert.Contains("unknown flag --all", (await Cli("list", "--all")).Err);

        Assert.Equal("[]", Json((await Cli("list")).Out).ToString());
    }

    [Fact]
    public async Task Every_flag_a_command_declares_is_accepted_and_help_works_on_every_command()
    {
        await Init();

        foreach (var command in CliCommands.All)
        {
            foreach (var flag in new[] { "--help", "-h" })
            {
                var (code, output, _) = await Cli(command.Name, flag);
                Assert.Equal(0, code);
                Assert.StartsWith("Usage: ", output);
            }
        }

        // The declared flags never trip the check (the commands may fail later for other reasons).
        Assert.DoesNotContain("unknown flag", (await Cli("list", "--status", "queued")).Err);
        Assert.DoesNotContain("unknown flag", (await Cli("decisions", "--status=active")).Err);
        Assert.DoesNotContain("unknown flag", (await Cli("findings", "--task", "1", "--decision", "1")).Err);
        Assert.DoesNotContain("unknown flag", (await Cli("requeue", "1", "--force")).Err);
        Assert.DoesNotContain("unknown flag", (await Cli("search", "x", "--all", "--type", "task", "--top", "3")).Err);
        Assert.DoesNotContain("unknown flag", (await Cli("messages", "--all", "--after", "0", "--top", "1")).Err);
        Assert.DoesNotContain("unknown flag", (await Cli("wait", "--task", "1", "--after", "0", "--seconds", "0")).Err);
        Assert.DoesNotContain("unknown flag", (await Cli("install", "agents", "--print")).Err);
        Assert.DoesNotContain("unknown flag", (await Cli("decide", "--title", "t", "--text", "x", "--rationale", "r", "--task-id", "1", "--supersede", "1")).Err);
    }

    [Fact]
    public async Task Search_prints_snippets_and_exits_3_when_nothing_matches()
    {
        await Init();
        await Cli("finding", "--title", "Köprü ölçümü", "--body", "p95 12 ms");
        await Cli("add", "--title", "Fix the bridge", "--scope", "köprü kırılıyor");

        var (code, output, _) = await Cli("search", "KOPRU");
        Assert.Equal(0, code);
        var hits = Json(output);
        Assert.Equal(2, hits.GetProperty("results").GetArrayLength());
        Assert.False(hits.GetProperty("more").GetBoolean());
        Assert.Equal(["task", "finding"], hits.GetProperty("results").EnumerateArray().Select(h => h.GetProperty("type").GetString()).Order().Reverse());

        var one = Json((await Cli("search", "kopru", "--type", "task", "--top", "1")).Out).GetProperty("results");
        Assert.Equal("scope", one.EnumerateArray().Single().GetProperty("matchedIn").GetString());
        Assert.Equal(2, Json((await Cli("search", "kopru", "--all")).Out).GetProperty("results").GetArrayLength());

        var none = await Cli("search", "kopru", "ankara");
        Assert.Equal(ExitCodes.NothingToDo, none.Code);
        Assert.Equal("", none.Out);

        Assert.Equal(ExitCodes.Error, (await Cli("search")).Code);
        Assert.Equal(ExitCodes.Error, (await Cli("search", "kopru", "--type", "chat")).Code);
        Assert.Equal(ExitCodes.Error, (await Cli("search", "kopru", "--top", "500")).Code);
        Assert.Equal(ExitCodes.Error, (await Cli("search", "kopru", "--top", "x")).Code);
    }
}
