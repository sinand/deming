using System.Text.Json;
using Deming.Cli;

namespace Deming.Tests;

/// <summary>
/// 'deming wt', 'deming merge', 'deming wt-remove' and the pre-push hook, against repositories made with 'git init'
/// in a temporary directory. Git runs with an empty global config of its own (GIT_CONFIG_GLOBAL) and no system
/// config, so neither the user's settings nor their hooks path take part; user.name and user.email are set in each
/// test repository.
/// </summary>
public sealed class WorktreeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "deming-wt-" + Guid.NewGuid().ToString("N"));
    private readonly Dictionary<string, string?> _gitEnv;

    public WorktreeTests()
    {
        Directory.CreateDirectory(_root);
        var globalConfig = Path.Combine(_root, "gitconfig");
        File.WriteAllText(globalConfig, "");
        _gitEnv = new Dictionary<string, string?>
        {
            ["GIT_CONFIG_GLOBAL"] = globalConfig,
            ["GIT_CONFIG_NOSYSTEM"] = "1",
            ["GIT_DIR"] = null,
            ["GIT_WORK_TREE"] = null,
        };
    }

    public void Dispose()
    {
        if (!Directory.Exists(_root))
            return;
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal); // git writes its objects read-only
        Directory.Delete(_root, true);
    }

    private string Repo => Path.Combine(_root, "repo");
    private string TreeOf(long id) => Path.Combine(_root, "repo-wt", $"task-{id}");

    private CliContext Context(StringWriter stdout, StringWriter stderr, string cwd, string? gitExecutable = null) => new()
    {
        Out = stdout,
        Err = stderr,
        Env = name => name == AgentInstall.DirVariable ? Path.Combine(_root, "commands") : null,
        CurrentDirectory = cwd,
        GitEnvironment = _gitEnv,
        GitExecutable = gitExecutable,
    };

    private async Task<(int Code, string Out, string Err)> Cli(string cwd, params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var code = await CliApp.RunAsync(args, Context(stdout, stderr, cwd));
        return (code, stdout.ToString().Trim(), stderr.ToString().Trim());
    }

    private GitResult Git(string dir, params string[] args) =>
        new Git(Context(new StringWriter(), new StringWriter(), dir)).Run(dir, args);

    private string GitOk(string dir, params string[] args)
    {
        var result = Git(dir, args);
        Assert.True(result.Ok, $"git {string.Join(' ', args)}: {result.Message}");
        return result.Out.Trim();
    }

    private void Commit(string dir, string file, string content, string message)
    {
        File.WriteAllText(Path.Combine(dir, file), content);
        GitOk(dir, "add", "--", file);
        GitOk(dir, "commit", "-q", "-m", message);
    }

    /// <summary>A repository on branch main with one commit, a .gitignore and an ignored .deming file.</summary>
    private string MakeRepo(string? include = null)
    {
        Directory.CreateDirectory(Repo);
        GitOk(Repo, "init", "-q", "-b", "main");
        GitOk(Repo, "config", "user.name", "Deming Test");
        GitOk(Repo, "config", "user.email", "test@example.invalid");
        File.WriteAllText(Path.Combine(Repo, ".gitignore"), ".deming\nlocal.json\n");
        File.WriteAllText(Path.Combine(Repo, ".deming"), "demo\n");
        GitOk(Repo, "add", ".gitignore");
        if (include is not null)
        {
            File.WriteAllText(Path.Combine(Repo, Worktree.IncludeFile), include);
            GitOk(Repo, "add", Worktree.IncludeFile);
        }
        Commit(Repo, "readme.txt", "hello\n", "first");
        return Repo;
    }

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    private string HookFile => Path.Combine(Repo, ".git", "hooks", Worktree.HookName);

    // ------------------------------------------------------------------ wt

    [Fact]
    public async Task Wt_opens_the_worktree_on_its_branch_copies_the_listed_files_and_installs_the_hook()
    {
        MakeRepo(include: "# needed to run\nlocal.json\n");
        File.WriteAllText(Path.Combine(Repo, "local.json"), "{\"secret\":\"\"}");

        var (code, output, err) = await Cli(Repo, "wt", "7");

        Assert.Equal(ExitCodes.Success, code);
        var result = Json(output);
        Assert.True(Worktree.SamePath(TreeOf(7), result.GetProperty("path").GetString()!), output);
        Assert.Equal("task-7", result.GetProperty("branch").GetString());
        Assert.Equal("main", result.GetProperty("base").GetString());
        Assert.Equal("installed", result.GetProperty("hook").GetString());
        Assert.Equal(["local.json", ".deming"], result.GetProperty("copied").EnumerateArray().Select(e => e.GetString()));
        Assert.Contains("deming merge", err);

        Assert.Equal("task-7", GitOk(TreeOf(7), "branch", "--show-current"));
        Assert.Equal("{\"secret\":\"\"}", File.ReadAllText(Path.Combine(TreeOf(7), "local.json")));
        Assert.Equal("demo\n", File.ReadAllText(Path.Combine(TreeOf(7), ".deming")));
        Assert.Equal(Worktree.HookScript, File.ReadAllText(HookFile));

        // A second call finds the worktree, says so (exit 4) and leaves the hook as it is.
        var again = await Cli(Repo, "wt", "7");
        Assert.Equal(ExitCodes.AlreadyExists, again.Code);
        Assert.False(Json(again.Out).GetProperty("created").GetBoolean());
        Assert.Equal("unchanged", Json(again.Out).GetProperty("hook").GetString());
    }

    [Fact]
    public async Task Wt_opens_nothing_when_a_listed_file_is_missing()
    {
        MakeRepo(include: "local.json\n");

        var (code, _, err) = await Cli(Repo, "wt", "3");

        Assert.Equal(ExitCodes.Error, code);
        Assert.Contains("local.json", err);
        Assert.False(Directory.Exists(TreeOf(3)));
        Assert.False(Git(Repo, "rev-parse", "--verify", "--quiet", "refs/heads/task-3").Ok);
        Assert.False(File.Exists(HookFile));
    }

    [Fact]
    public async Task Wt_refuses_an_include_path_outside_the_repository()
    {
        MakeRepo(include: "../elsewhere.txt\n");

        var (code, _, err) = await Cli(Repo, "wt", "3");

        Assert.Equal(ExitCodes.Error, code);
        Assert.Contains("not a path inside the repository", err);
        Assert.False(Directory.Exists(TreeOf(3)));
    }

    [Fact]
    public async Task Wt_never_overwrites_the_projects_own_pre_push_and_opens_nothing()
    {
        MakeRepo();
        var own = "#!/bin/sh\necho mine\nexit 0\n";
        Directory.CreateDirectory(Path.GetDirectoryName(HookFile)!);
        File.WriteAllText(HookFile, own);

        var (code, _, err) = await Cli(Repo, "wt", "2");

        Assert.Equal(ExitCodes.Error, code);
        Assert.Contains(Worktree.LocalHookName, err);
        Assert.Equal(own, File.ReadAllText(HookFile));
        Assert.False(Directory.Exists(TreeOf(2)));
    }

    [Fact]
    public async Task Wt_writes_the_hook_where_core_hooksPath_points()
    {
        MakeRepo();
        var hooks = Path.Combine(_root, "shared-hooks");
        GitOk(Repo, "config", "core.hooksPath", hooks);

        Assert.Equal(ExitCodes.Success, (await Cli(Repo, "wt", "4")).Code);

        Assert.Equal(Worktree.HookScript, File.ReadAllText(Path.Combine(hooks, Worktree.HookName)));
        Assert.False(File.Exists(HookFile));
    }

    [Fact]
    public async Task Wt_reuses_an_existing_branch_from_inside_another_worktree()
    {
        MakeRepo();
        GitOk(Repo, "branch", "task-5");
        Assert.Equal(ExitCodes.Success, (await Cli(Repo, "wt", "1")).Code);

        var (code, output, _) = await Cli(TreeOf(1), "wt", "5");

        Assert.Equal(ExitCodes.Success, code);
        Assert.Equal(JsonValueKind.Null, Json(output).GetProperty("base").ValueKind);
        Assert.Equal("task-5", GitOk(TreeOf(5), "branch", "--show-current"));
    }

    [Fact]
    public async Task Outside_a_repository_or_without_git_the_error_says_so()
    {
        var plain = Path.Combine(_root, "plain");
        Directory.CreateDirectory(plain);

        var notRepo = await Cli(plain, "wt", "1");
        Assert.Equal(ExitCodes.Error, notRepo.Code);
        Assert.Contains("not inside a git repository", notRepo.Err);

        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var code = await CliApp.RunAsync(["merge"], Context(stdout, stderr, plain, gitExecutable: Path.Combine(_root, "no-such-git")));
        Assert.Equal(ExitCodes.Error, code);
        Assert.Contains("git was not found", stderr.ToString());

        Assert.Equal(ExitCodes.Error, (await Cli(plain, "wt")).Code);
        Assert.Equal(ExitCodes.Error, (await Cli(plain, "wt-remove", "x")).Code);
    }

    // ------------------------------------------------------------------ push hook

    [Fact]
    public async Task The_hook_refuses_a_push_from_a_worktree_and_runs_pre_push_local_in_the_main_one()
    {
        MakeRepo();
        var remote = Path.Combine(_root, "remote.git");
        GitOk(_root, "init", "-q", "--bare", "-b", "main", remote);
        GitOk(Repo, "remote", "add", "origin", remote);
        Assert.Equal(ExitCodes.Success, (await Cli(Repo, "wt", "8")).Code);
        var marker = Path.Combine(_root, "local-ran.txt");
        File.WriteAllText(Path.Combine(Repo, ".git", "hooks", Worktree.LocalHookName),
            "#!/bin/sh\necho ran > \"" + marker.Replace('\\', '/') + "\"\nexit 0\n");

        Commit(TreeOf(8), "work.txt", "work\n", "work");
        var fromTree = Git(TreeOf(8), "push", "origin", "task-8");
        Assert.False(fromTree.Ok);
        Assert.Contains("push refused", fromTree.Err);
        Assert.False(File.Exists(marker));
        Assert.False(Git(remote, "rev-parse", "--verify", "--quiet", "refs/heads/task-8").Ok);

        var fromMain = Git(Repo, "push", "origin", "main");
        Assert.True(fromMain.Ok, fromMain.Message);
        Assert.True(File.Exists(marker));
    }

    // ------------------------------------------------------------------ merge

    [Fact]
    public async Task Merge_rebases_inside_the_worktree_and_fast_forwards_main_then_wt_remove_cleans_up()
    {
        MakeRepo();
        Assert.Equal(ExitCodes.Success, (await Cli(Repo, "wt", "9")).Code);
        Commit(TreeOf(9), "feature.txt", "feature\n", "feature");
        Commit(Repo, "other.txt", "other\n", "meanwhile on main");

        var (code, output, _) = await Cli(TreeOf(9), "merge");

        Assert.Equal(ExitCodes.Success, code);
        var result = Json(output);
        Assert.True(result.GetProperty("merged").GetBoolean());
        Assert.True(result.GetProperty("rebased").GetBoolean());
        Assert.Equal("main", result.GetProperty("main").GetString());
        var head = GitOk(Repo, "rev-parse", "main");
        Assert.Equal(head, result.GetProperty("commit").GetString());
        Assert.Equal(head, GitOk(Repo, "rev-parse", "task-9"));
        Assert.True(File.Exists(Path.Combine(Repo, "feature.txt"))); // the main worktree moved with its branch
        Assert.Equal("", GitOk(Repo, "status", "--porcelain"));

        var removed = await Cli(Repo, "wt-remove", "9");
        Assert.Equal(ExitCodes.Success, removed.Code);
        Assert.True(Json(removed.Out).GetProperty("removedWorktree").GetBoolean());
        Assert.False(Directory.Exists(TreeOf(9)));
        Assert.False(Directory.Exists(Path.Combine(_root, "repo-wt")));
        Assert.False(Git(Repo, "rev-parse", "--verify", "--quiet", "refs/heads/task-9").Ok);
    }

    [Fact]
    public async Task Merge_with_an_id_works_from_the_main_worktree()
    {
        MakeRepo();
        Assert.Equal(ExitCodes.Success, (await Cli(Repo, "wt", "10")).Code);
        Commit(TreeOf(10), "feature.txt", "feature\n", "feature");

        var (code, output, _) = await Cli(Repo, "merge", "10");

        Assert.Equal(ExitCodes.Success, code);
        Assert.False(Json(output).GetProperty("rebased").GetBoolean());
        Assert.Equal(GitOk(Repo, "rev-parse", "task-10"), GitOk(Repo, "rev-parse", "main"));
    }

    [Fact]
    public async Task Merge_stops_on_a_conflict_names_the_file_and_leaves_nothing_half_done()
    {
        MakeRepo();
        Assert.Equal(ExitCodes.Success, (await Cli(Repo, "wt", "11")).Code);
        Commit(TreeOf(11), "readme.txt", "from the task\n", "task edit");
        Commit(Repo, "readme.txt", "from main\n", "main edit");
        var taskBefore = GitOk(Repo, "rev-parse", "task-11");
        var mainBefore = GitOk(Repo, "rev-parse", "main");

        var (code, _, err) = await Cli(TreeOf(11), "merge");

        Assert.Equal(ExitCodes.Error, code);
        Assert.Contains("readme.txt", err);
        Assert.Contains("conflict", err);
        Assert.Equal(taskBefore, GitOk(Repo, "rev-parse", "task-11"));
        Assert.Equal(mainBefore, GitOk(Repo, "rev-parse", "main"));
        Assert.Equal("", GitOk(TreeOf(11), "status", "--porcelain", "--untracked-files=no"));
        var rebaseDir = GitOk(TreeOf(11), "rev-parse", "--path-format=absolute", "--git-path", "rebase-merge");
        Assert.False(Directory.Exists(rebaseDir));
        Assert.Equal("from the task\n", File.ReadAllText(Path.Combine(TreeOf(11), "readme.txt")));
    }

    [Fact]
    public async Task Merge_refuses_uncommitted_changes_and_does_nothing_in_the_main_worktree()
    {
        MakeRepo();
        Assert.Equal(ExitCodes.Success, (await Cli(Repo, "wt", "12")).Code);
        File.WriteAllText(Path.Combine(TreeOf(12), "readme.txt"), "edited, not committed\n");
        var mainBefore = GitOk(Repo, "rev-parse", "main");

        var dirty = await Cli(TreeOf(12), "merge");
        Assert.Equal(ExitCodes.Error, dirty.Code);
        Assert.Contains("uncommitted", dirty.Err);
        Assert.Equal(mainBefore, GitOk(Repo, "rev-parse", "main"));

        var inMain = await Cli(Repo, "merge");
        Assert.Equal(ExitCodes.Success, inMain.Code);
        Assert.False(Json(inMain.Out).GetProperty("merged").GetBoolean());

        Assert.Equal(ExitCodes.Error, (await Cli(Repo, "merge", "99")).Code);
    }

    // ------------------------------------------------------------------ wt-remove

    [Fact]
    public async Task Wt_remove_refuses_an_unmerged_branch_and_a_call_from_inside()
    {
        MakeRepo();
        Assert.Equal(ExitCodes.Success, (await Cli(Repo, "wt", "13")).Code);
        Commit(TreeOf(13), "feature.txt", "feature\n", "unmerged work");

        var unmerged = await Cli(Repo, "wt-remove", "13");
        Assert.Equal(ExitCodes.Error, unmerged.Code);
        Assert.Contains("not merged", unmerged.Err);
        Assert.True(Directory.Exists(TreeOf(13)));
        Assert.True(Git(Repo, "rev-parse", "--verify", "--quiet", "refs/heads/task-13").Ok);

        Assert.Equal(ExitCodes.Success, (await Cli(TreeOf(13), "merge")).Code);
        var inside = await Cli(TreeOf(13), "wt-remove", "13");
        Assert.Equal(ExitCodes.Error, inside.Code);
        Assert.Contains("main worktree", inside.Err);
        Assert.True(Directory.Exists(TreeOf(13)));

        Assert.Equal(ExitCodes.Error, (await Cli(Repo, "wt-remove", "77")).Code);
    }

    [Fact]
    public async Task Wt_remove_keeps_a_worktree_with_uncommitted_work()
    {
        MakeRepo();
        Assert.Equal(ExitCodes.Success, (await Cli(Repo, "wt", "14")).Code);
        File.WriteAllText(Path.Combine(TreeOf(14), "draft.txt"), "not committed\n");

        var (code, _, err) = await Cli(Repo, "wt-remove", "14");

        Assert.Equal(ExitCodes.Error, code);
        Assert.Contains("nothing was removed", err);
        Assert.True(File.Exists(Path.Combine(TreeOf(14), "draft.txt")));
        Assert.True(Git(Repo, "rev-parse", "--verify", "--quiet", "refs/heads/task-14").Ok);
    }
}
