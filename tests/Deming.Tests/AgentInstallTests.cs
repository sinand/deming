using System.Text.Json;
using Deming.Cli;

namespace Deming.Tests;

/// <summary>
/// 'deming install claude' and 'deming install agents'. Every write goes to a temporary directory: the commands
/// through DEMING_CLAUDE_DIR, AGENTS.md through a temporary repository as the current directory.
/// </summary>
public sealed class AgentInstallTests : IDisposable
{
    private static readonly string[] Commands = ["next", "done", "block", "cancel", "add", "decide", "decisions", "finding", "findings", "search"];

    private readonly string _root = Path.Combine(Path.GetTempPath(), "deming-agents-" + Guid.NewGuid().ToString("N"));
    private string Dir => Path.Combine(_root, "commands");
    private string Repo => Path.Combine(_root, "repo");

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, true);
    }

    private CliContext Context(StringWriter? stdout = null, StringWriter? stderr = null, string? cwd = null) => new()
    {
        Out = stdout ?? new StringWriter(),
        Err = stderr ?? new StringWriter(),
        Env = name => name == AgentInstall.DirVariable ? Dir : null,
        CurrentDirectory = cwd ?? Path.GetTempPath(),
    };

    private string MakeRepo()
    {
        Directory.CreateDirectory(Path.Combine(Repo, ".git"));
        Directory.CreateDirectory(Path.Combine(Repo, "src", "deep"));
        return Repo;
    }

    [Fact]
    public void Shipped_commands_are_prefixed_and_take_no_positional_arguments()
    {
        var shipped = AgentInstall.Shipped();

        Assert.Equal(Commands.Select(c => $"deming-{c}.md").Order(StringComparer.Ordinal), shipped.Keys);
        foreach (var (name, content) in shipped)
        {
            var text = System.Text.Encoding.UTF8.GetString(content);
            Assert.Contains("$ARGUMENTS", text);
            Assert.DoesNotContain("$1", text);
            Assert.DoesNotContain("$2", text);
            Assert.DoesNotContain("/deming:", text);
        }
    }

    [Fact]
    public void Claude_dir_comes_from_the_option_then_the_variables_then_the_home_directory()
    {
        var config = Path.Combine(_root, "config");
        Func<string, string?> both = name => name switch
        {
            AgentInstall.DirVariable => Dir,
            AgentInstall.ClaudeConfigVariable => config,
            _ => null,
        };
        Func<string, string?> configOnly = name => name == AgentInstall.ClaudeConfigVariable ? config : null;

        Assert.Equal(Path.GetFullPath(_root), AgentInstall.ClaudeDir(both, _root));
        Assert.Equal(Path.GetFullPath(Dir), AgentInstall.ClaudeDir(both));
        Assert.Equal(Path.Combine(Path.GetFullPath(config), "commands"), AgentInstall.ClaudeDir(configOnly));
        Assert.Equal(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "commands"),
            AgentInstall.ClaudeDir(_ => null));
    }

    [Fact]
    public async Task Install_claude_writes_the_commands_and_prints_each_path()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var code = await CliApp.RunAsync(["install", "claude"], Context(stdout, stderr));

        Assert.Equal(ExitCodes.Success, code);
        using var result = JsonDocument.Parse(stdout.ToString());
        var written = result.RootElement.GetProperty("written").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Equal(Commands.Length, written.Count);
        foreach (var name in Commands)
        {
            var path = Path.Combine(Dir, $"deming-{name}.md");
            Assert.True(File.Exists(path), path);
            Assert.Contains(path, written);
            Assert.Contains($"wrote {path}", stderr.ToString());
        }
        Assert.Contains("/deming-next", stderr.ToString());
        // Nothing left to run by hand: no registration commands are printed.
        Assert.False(result.RootElement.TryGetProperty("next", out _));
        Assert.Empty(AgentInstall.Stale(Dir));
    }

    [Fact]
    public async Task Install_claude_honours_the_dir_option()
    {
        var other = Path.Combine(_root, "other");

        await CliApp.RunAsync(["install", "claude", "--dir", other], Context());

        Assert.True(File.Exists(Path.Combine(other, "deming-next.md")));
        Assert.False(Directory.Exists(Dir));
    }

    [Fact]
    public async Task Install_without_target_prints_usage_and_writes_nothing()
    {
        var stderr = new StringWriter();

        var code = await CliApp.RunAsync(["install"], Context(stderr: stderr));

        Assert.Equal(ExitCodes.Error, code);
        Assert.Contains("deming install claude", stderr.ToString());
        Assert.Contains("deming install agents", stderr.ToString());
        Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public async Task Install_claude_drops_retired_commands_repairs_edited_ones_and_leaves_other_files()
    {
        Directory.CreateDirectory(Dir);
        var own = Path.Combine(Dir, "next.md");
        File.WriteAllText(own, "the user's own command");
        await CliApp.RunAsync(["install", "claude"], Context());
        var retired = Path.Combine(Dir, "deming-retired.md");
        File.WriteAllText(retired, "---\ndescription: old\n---\n" + AgentInstall.CommandMarker + "\nold");
        File.WriteAllText(Path.Combine(Dir, "deming-next.md"), AgentInstall.CommandMarker + "\nedited");
        Assert.Equal(["deming-next.md", "deming-retired.md"], AgentInstall.Stale(Dir));
        var stdout = new StringWriter();

        await CliApp.RunAsync(["install", "claude"], Context(stdout));

        Assert.False(File.Exists(retired));
        Assert.Contains("deming-retired.md", stdout.ToString());
        Assert.Empty(AgentInstall.Stale(Dir));
        Assert.Equal("the user's own command", File.ReadAllText(own));
    }

    [Fact]
    public void Every_shipped_command_carries_the_marker_after_its_front_matter()
    {
        foreach (var (name, content) in AgentInstall.Shipped())
        {
            var lines = System.Text.Encoding.UTF8.GetString(content).Split('\n');
            Assert.Equal("---", lines[0].TrimEnd());
            var close = Array.FindIndex(lines, 1, l => l.TrimEnd() == "---");
            Assert.True(close > 0, name);
            Assert.Equal(AgentInstall.CommandMarker, lines[close + 1].TrimEnd());
        }
    }

    [Fact]
    public async Task Install_claude_never_deletes_the_users_own_deming_file()
    {
        Directory.CreateDirectory(Dir);
        var mine = Path.Combine(Dir, "deming-mine.md");
        File.WriteAllText(mine, "my own command");
        var stdout = new StringWriter();

        var code = await CliApp.RunAsync(["install", "claude"], Context(stdout));

        Assert.Equal(ExitCodes.Success, code);
        Assert.Equal("my own command", File.ReadAllText(mine));
        Assert.DoesNotContain("deming-mine.md", stdout.ToString());
        Assert.True(File.Exists(Path.Combine(Dir, "deming-next.md")));
        // Their file is not Deming's, so it does not make the install look stale either.
        Assert.Empty(AgentInstall.Stale(Dir));
    }

    [Fact]
    public async Task A_users_file_with_a_shipped_name_is_an_error_and_nothing_is_written()
    {
        Directory.CreateDirectory(Dir);
        var clash = Path.Combine(Dir, "deming-next.md");
        File.WriteAllText(clash, "my own next");
        var stderr = new StringWriter();

        var code = await CliApp.RunAsync(["install", "claude"], Context(stderr: stderr));

        Assert.Equal(ExitCodes.Error, code);
        Assert.Contains("deming-next.md", stderr.ToString());
        Assert.Contains("was not written by Deming", stderr.ToString());
        Assert.Equal("my own next", File.ReadAllText(clash));
        Assert.Equal([clash], Directory.GetFiles(Dir));
    }

    [Fact]
    public async Task The_stale_warning_ignores_unmarked_files()
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(Path.Combine(Dir, "deming-mine.md"), "my own command");
        var stderr = new StringWriter();

        await CliApp.RunAsync(["show"], Context(stderr: stderr));

        Assert.DoesNotContain("warning", stderr.ToString());
    }

    [Fact]
    public async Task Stale_copy_warns_on_every_command_and_does_not_stop_it()
    {
        await CliApp.RunAsync(["install", "claude"], Context());
        File.WriteAllText(Path.Combine(Dir, "deming-done.md"), AgentInstall.CommandMarker + "\nold copy");
        var stderr = new StringWriter();

        // "show" without an id fails on its own; the warning must come first and the exit code stay the command's.
        var code = await CliApp.RunAsync(["show"], Context(stderr: stderr));

        Assert.Equal(ExitCodes.Error, code);
        Assert.Contains("warning: the installed Claude Code commands", stderr.ToString());
        Assert.Contains("deming install claude", stderr.ToString());
        Assert.Contains("Missing task id", stderr.ToString());
    }

    [Fact]
    public async Task Current_or_missing_copy_prints_no_warning()
    {
        // A command directory with only the user's own commands is "not installed".
        Directory.CreateDirectory(Dir);
        File.WriteAllText(Path.Combine(Dir, "next.md"), "the user's own command");
        var stderr = new StringWriter();
        await CliApp.RunAsync(["show"], Context(stderr: stderr));
        Assert.DoesNotContain("warning", stderr.ToString());

        await CliApp.RunAsync(["install", "claude"], Context());
        stderr = new StringWriter();
        await CliApp.RunAsync(["show"], Context(stderr: stderr));
        Assert.DoesNotContain("warning", stderr.ToString());
    }

    [Fact]
    public void Agents_block_is_marked_and_agent_neutral()
    {
        var block = AgentInstall.Block();

        Assert.StartsWith(AgentInstall.BlockBegin + "\n", block);
        Assert.EndsWith("\n" + AgentInstall.BlockEnd, block);
        foreach (var word in new[] { "deming next", "deming take", "deming done", "deming block", "DEMING_SESSION_ID" })
            Assert.Contains(word, block);
        foreach (var word in new[] { "Claude", "$ARGUMENTS", "/deming-" })
            Assert.DoesNotContain(word, block);
    }

    [Fact]
    public async Task Install_agents_creates_AGENTS_md_at_the_repository_root_and_a_rerun_changes_nothing()
    {
        var repo = MakeRepo();
        var file = Path.Combine(repo, "AGENTS.md");
        var stdout = new StringWriter();

        var code = await CliApp.RunAsync(["install", "agents"], Context(stdout, cwd: Path.Combine(repo, "src", "deep")));

        Assert.Equal(ExitCodes.Success, code);
        Assert.Contains("\"created\"", stdout.ToString());
        Assert.Equal(AgentInstall.Block() + "\n", File.ReadAllText(file));
        var written = File.GetLastWriteTimeUtc(file);
        File.SetLastWriteTimeUtc(file, written.AddMinutes(-5));
        stdout = new StringWriter();

        await CliApp.RunAsync(["install", "agents"], Context(stdout, cwd: repo));

        Assert.Contains("\"unchanged\"", stdout.ToString());
        Assert.Equal(written.AddMinutes(-5), File.GetLastWriteTimeUtc(file));
        Assert.False(Directory.Exists(Dir));
    }

    [Fact]
    public async Task Install_agents_updates_the_block_in_place_and_keeps_the_rest()
    {
        var repo = MakeRepo();
        var file = Path.Combine(repo, "AGENTS.md");
        var before = "# Rules\r\n\r\nUse tabs.\r\n\r\n";
        var after = "\r\n\r\n## Later\r\n\r\nKeep this.\r\n";
        File.WriteAllText(file, before + AgentInstall.BlockBegin + "\r\nold text\r\n" + AgentInstall.BlockEnd + after);

        await CliApp.RunAsync(["install", "agents"], Context(cwd: repo));

        var expected = before + AgentInstall.Block().Replace("\n", "\r\n") + after;
        Assert.Equal(expected, File.ReadAllText(file));
    }

    [Fact]
    public void Block_is_appended_to_a_file_without_markers()
    {
        var text = AgentInstall.WithBlock("# Rules\n\nUse tabs.\n");

        Assert.Equal("# Rules\n\nUse tabs.\n\n" + AgentInstall.Block() + "\n", text);
        Assert.Equal(text, AgentInstall.WithBlock(text));
    }

    [Fact]
    public async Task Install_agents_refuses_a_begin_marker_without_an_end()
    {
        var repo = MakeRepo();
        var file = Path.Combine(repo, "AGENTS.md");
        File.WriteAllText(file, "x\n" + AgentInstall.BlockBegin + "\nhalf\n");
        var stderr = new StringWriter();

        var code = await CliApp.RunAsync(["install", "agents"], Context(stderr: stderr, cwd: repo));

        Assert.Equal(ExitCodes.Error, code);
        Assert.Contains(AgentInstall.BlockEnd, stderr.ToString());
        Assert.Equal("x\n" + AgentInstall.BlockBegin + "\nhalf\n", File.ReadAllText(file));
    }

    [Fact]
    public async Task Install_agents_print_writes_nothing()
    {
        var repo = MakeRepo();
        var stdout = new StringWriter();

        var code = await CliApp.RunAsync(["install", "agents", "--print"], Context(stdout, cwd: repo));

        Assert.Equal(ExitCodes.Success, code);
        Assert.Equal(AgentInstall.Block(), stdout.ToString().TrimEnd('\r', '\n'));
        Assert.False(File.Exists(Path.Combine(repo, "AGENTS.md")));
    }
}
