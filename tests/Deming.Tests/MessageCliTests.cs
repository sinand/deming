using System.Security.Cryptography;
using System.Text.Json;
using Deming.Cli;
using Deming.Data;
using Deming.Server;
using Deming.Server.Api;
using Microsoft.AspNetCore.Builder;

namespace Deming.Tests;

/// <summary>message, wait and messages against a real server; cursors go to a temporary data directory.</summary>
public sealed class MessageCliTests : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _dbPath = TempDatabase.NewPath();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "deming-tests", Guid.NewGuid().ToString("N"));
    private readonly string _key = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
    private readonly SocketsHttpHandler _handler = TestCertificate.Handler();
    private WebApplication _app = null!;
    private Dictionary<string, string> _env = null!;

    private string DataDir => Path.Combine(_dir, "data");

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
            ["DEMING_AGENT"] = "claude",
            ["DEMING_CONFIG"] = Path.Combine(_dir, "config.json"), // never the real user directory
        };
        Assert.Equal(0, (await Cli("init", "demo")).Code);
    }

    public async ValueTask DisposeAsync()
    {
        _handler.Dispose();
        await _app.DisposeAsync();
        TempDatabase.DeleteFiles(_dbPath, new DemingDb(_dbPath).ConnectionString);
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private Task<(int Code, string Out, string Err)> Cli(params string[] args) => CliAs(_env, null, args);

    private Dictionary<string, string> As(string agent, string session) =>
        new(_env) { ["DEMING_AGENT"] = agent, ["DEMING_SESSION_ID"] = session };

    private async Task<(int Code, string Out, string Err)> CliAs(Dictionary<string, string> env, string? stdin, params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var code = await CliApp.RunAsync(args, new CliContext
        {
            Out = stdout,
            Err = stderr,
            In = new StringReader(stdin ?? ""),
            Env = name => env.GetValueOrDefault(name),
            CurrentDirectory = _dir,
            HttpHandler = _handler,
            DataDirectory = DataDir,
        });
        return (code, stdout.ToString().Trim(), stderr.ToString().Trim());
    }

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    private static string[] Lines(string text) => text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    [Fact]
    public async Task Message_needs_the_agent_label_and_sends_nothing_without_it()
    {
        var env = new Dictionary<string, string>(_env);
        env.Remove("DEMING_AGENT");

        var (code, _, err) = await CliAs(env, null, "message", "hello");

        Assert.Equal(ExitCodes.Error, code);
        Assert.Contains("DEMING_AGENT", err);
        Assert.Equal(0, Json((await Cli("messages")).Out).GetProperty("messages").GetArrayLength());
    }

    [Fact]
    public async Task Message_takes_words_a_file_or_stdin_and_messages_reads_them()
    {
        var file = Path.Combine(_dir, "note.txt");
        await File.WriteAllTextAsync(file, "from a file: /c/path stays as written", Ct);

        var words = await Cli("message", "--to", "Codex", "the", "build", "is", "green");
        Assert.Equal(0, words.Code);
        var posted = Json(words.Out);
        Assert.Equal("codex", posted.GetProperty("recipient").GetString());
        Assert.False(posted.TryGetProperty("body", out _));

        Assert.Equal(0, (await Cli("message", "note.txt")).Code);
        Assert.Equal(0, (await CliAs(_env, "/c/Users from stdin", "message", "-")).Code);
        var reply = await Cli("message", "--reply", posted.GetProperty("id").GetInt64().ToString(), "answer");
        Assert.Equal(0, reply.Code);

        var list = Json((await Cli("messages")).Out).GetProperty("messages");
        Assert.Equal(["the build is green", "from a file: /c/path stays as written", "/c/Users from stdin", "answer"],
            list.EnumerateArray().Select(m => m.GetProperty("body").GetString()));

        var paged = Json((await Cli("messages", "--top", "2", "--after", list[0].GetProperty("id").GetInt64().ToString())).Out);
        Assert.Equal(2, paged.GetProperty("messages").GetArrayLength());
        Assert.True(paged.GetProperty("hasMore").GetBoolean());

        var one = Json((await Cli("messages", list[3].GetProperty("id").GetInt64().ToString())).Out);
        Assert.Equal(list[0].GetProperty("id").GetInt64(), one.GetProperty("replyToId").GetInt64());
    }

    [Fact]
    public async Task Task_channel_and_all()
    {
        var task = Json((await Cli("add", "--title", "Work")).Out).GetProperty("id").GetInt64().ToString();
        Assert.Equal(0, (await Cli("message", "--task", task, "in the task")).Code);
        Assert.Equal(0, (await Cli("message", "in the project")).Code);

        Assert.Equal("in the task", Json((await Cli("messages", "--task", task)).Out).GetProperty("messages")[0].GetProperty("body").GetString());
        Assert.Equal(1, Json((await Cli("messages")).Out).GetProperty("messages").GetArrayLength());
        Assert.Equal(2, Json((await Cli("messages", "--all")).Out).GetProperty("messages").GetArrayLength());
        Assert.Equal(ExitCodes.Error, (await Cli("messages", "--task", task, "--all")).Code);
    }

    [Fact]
    public async Task Wait_prints_the_next_message_then_the_data_line_and_moves_its_own_cursor_only()
    {
        var codexA = As("codex", "codex-1");
        Assert.Equal(0, (await Cli("message", "--to", "codex", "first")).Code);

        var first = await CliAs(codexA, null, "wait", "--seconds", "5");
        Assert.Equal(0, first.Code);
        var lines = Lines(first.Out);
        Assert.Equal(MessageCommands.DataNotInstructions, lines[^1]);
        Assert.Equal("first", Json(lines[0]).GetProperty("messages")[0].GetProperty("body").GetString());

        // The cursor moved past "first": the next wait blocks until a new message arrives.
        var waiting = CliAs(codexA, null, "wait", "--seconds", "30");
        await Task.Delay(300, Ct);
        Assert.False(waiting.IsCompleted);
        Assert.Equal(0, (await CliAs(codexA, null, "message", "codex to everyone does not wake codex")).Code);
        await Task.Delay(300, Ct);
        Assert.False(waiting.IsCompleted);
        Assert.Equal(0, (await Cli("message", "second")).Code);
        var second = await waiting;
        Assert.Equal(0, second.Code);
        Assert.Equal("second", Json(Lines(second.Out)[0]).GetProperty("messages")[0].GetProperty("body").GetString());

        // Another session with the same label has its own cursor and starts from the beginning.
        var codexB = await CliAs(As("codex", "codex-2"), null, "wait", "--seconds", "1");
        Assert.Equal("first", Json(Lines(codexB.Out)[0]).GetProperty("messages")[0].GetProperty("body").GetString());

        // --after reads from there and leaves the cursor alone; so does messages.
        var explicitAfter = await CliAs(codexA, null, "wait", "--after", "0", "--seconds", "1");
        Assert.Equal(0, explicitAfter.Code);
        await Cli("messages");
        var timedOut = await CliAs(codexA, null, "wait", "--seconds", "1");
        Assert.Equal(ExitCodes.NothingToDo, timedOut.Code);
        Assert.Equal("", timedOut.Out);
        Assert.Contains("No message in 1 seconds", timedOut.Err);

        Assert.True(Directory.Exists(Path.Combine(DataDir, "cursors", "codex-1")));
    }

    [Fact]
    public async Task Wait_without_a_session_needs_after_and_an_unreachable_server_is_an_error_not_silence()
    {
        var noSession = new Dictionary<string, string>(_env);
        noSession.Remove("DEMING_SESSION_ID");
        var (code, _, err) = await CliAs(noSession, null, "wait", "--seconds", "0");
        Assert.Equal(ExitCodes.Error, code);
        Assert.Contains("DEMING_SESSION_ID", err);

        var withAfter = await CliAs(noSession, null, "wait", "--after", "0", "--seconds", "0");
        Assert.Equal(ExitCodes.NothingToDo, withAfter.Code);

        // No server at the address: exit 1, never 3 ("no message").
        var dead = new Dictionary<string, string>(_env) { ["DEMING_URL"] = "https://127.0.0.1:1" };
        var unreachable = await CliAs(dead, null, "wait", "--seconds", "1");
        Assert.Equal(ExitCodes.Error, unreachable.Code);
        Assert.Contains("Cannot reach the server", unreachable.Err);
    }
}
