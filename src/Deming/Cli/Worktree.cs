using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

namespace Deming.Cli;

/// <summary>
/// Parallel work with git worktrees: 'deming wt', 'deming merge' and 'deming wt-remove'. All of it is local git,
/// the server is never called. Each task gets its own worktree beside the repository (&lt;repo&gt;-wt/task-&lt;id&gt;)
/// on branch task-&lt;id&gt;, so two agents never see each other's half-done edits. "Done" means "on the main
/// branch": merge rebases the task branch inside its own worktree (the session that knows the task resolves the
/// conflict there), then moves the main branch with a fast-forward only.
/// A pre-push hook keeps workers from pushing: it refuses any push from a linked worktree.
/// </summary>
internal static class Worktree
{
    public const string IncludeFile = ".worktreeinclude";
    public const string HookName = "pre-push";
    public const string LocalHookName = "pre-push.local";
    public const string HookMarker = "# deming-pre-push";

    /// <summary>The pre-push hook 'deming wt' writes. Plain sh: git runs it with its own sh on Windows too.</summary>
    public const string HookScript =
        "#!/bin/sh\n"
        + HookMarker + ": written by 'deming wt', which rewrites it; put a hook of your own in pre-push.local.\n"
        + "# Refuses a push from a linked worktree: a task branch is merged with 'deming merge' and pushed from the\n"
        + "# main worktree. In the main worktree it runs pre-push.local when there is one, with the same input.\n"
        + "git_dir=$(cd \"$(git rev-parse --git-dir)\" && pwd -P)\n"
        + "common_dir=$(cd \"$(git rev-parse --git-common-dir)\" && pwd -P)\n"
        + "if [ \"$git_dir\" != \"$common_dir\" ]; then\n"
        + "    echo \"deming: push refused from the linked worktree $(pwd). Merge it with 'deming merge', then push from the main worktree.\" >&2\n"
        + "    exit 1\n"
        + "fi\n"
        + "local_hook=\"$(dirname \"$0\")/pre-push.local\"\n"
        + "if [ -f \"$local_hook\" ]; then\n"
        + "    if [ -x \"$local_hook\" ]; then\n"
        + "        exec \"$local_hook\" \"$@\"\n"
        + "    fi\n"
        + "    exec sh \"$local_hook\" \"$@\"\n"
        + "fi\n"
        + "exit 0\n";

    public static string BranchName(long id) => $"task-{id}";

    /// <summary>Where 'deming wt' puts a task's worktree: beside the main worktree, in &lt;name&gt;-wt/task-&lt;id&gt;.</summary>
    public static string WorktreePath(string mainRoot, long id)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(mainRoot));
        return Path.Combine(Path.GetDirectoryName(root) ?? root, Path.GetFileName(root) + "-wt", BranchName(id));
    }

    // ---- deming wt <id>

    public static Task<int> Create(CliRun run)
    {
        var id = run.RequireId();
        var git = new Git(run.Ctx);
        var repo = Repo.Open(git, run.Ctx.CurrentDirectory);
        var main = repo.Main;
        var branch = BranchName(id);

        // Everything that can stop the command is checked before anything is written.
        var includes = ReadIncludes(main.Path);
        var missing = includes.Where(p => !File.Exists(Path.Combine(main.Path, p)) && !Directory.Exists(Path.Combine(main.Path, p))).ToList();
        if (missing.Count > 0)
            throw new CliException($"{IncludeFile} lists what a worktree needs, but {string.Join(", ", missing)} is missing in {main.Path}. Create it first; no worktree was created.");

        var hook = InstallHook(git, main.Path);

        if (repo.Trees.FirstOrDefault(t => t.Branch == branch) is { } existing)
        {
            run.Ctx.Out.WriteLine(new JsonObject { ["path"] = existing.Path, ["branch"] = branch, ["created"] = false, ["hook"] = hook }.ToJsonString());
            run.Ctx.Err.WriteLine($"{branch} already has a worktree at {existing.Path}.");
            return Task.FromResult(ExitCodes.AlreadyExists);
        }

        var path = WorktreePath(main.Path, id);
        if (Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any())
            throw new CliException($"{path} already exists and is not a worktree of {branch}. Move it away first; no worktree was created.");

        var branchExists = git.Run(main.Path, "rev-parse", "--verify", "--quiet", "refs/heads/" + branch).Ok;
        var baseBranch = main.RequireBranch();
        if (branchExists)
            git.Need(main.Path, "add the worktree", "worktree", "add", path, branch);
        else
            git.Need(main.Path, "add the worktree", "worktree", "add", "-b", branch, path, baseBranch);

        var copied = new List<string>();
        foreach (var entry in includes)
        {
            Copy(Path.Combine(main.Path, entry), Path.Combine(path, entry));
            copied.Add(entry);
        }
        // The project name comes from .deming; a worktree outside the repository would not find it otherwise.
        var demingFile = Path.Combine(main.Path, ".deming");
        if (File.Exists(demingFile) && !File.Exists(Path.Combine(path, ".deming")))
        {
            File.Copy(demingFile, Path.Combine(path, ".deming"));
            copied.Add(".deming");
        }

        run.Ctx.Out.WriteLine(new JsonObject
        {
            ["path"] = path,
            ["branch"] = branch,
            ["base"] = branchExists ? null : baseBranch,
            ["created"] = true,
            ["hook"] = hook,
            ["copied"] = new JsonArray(copied.Select(c => (JsonNode?)JsonValue.Create(c)).ToArray()),
        }.ToJsonString());
        run.Ctx.Err.WriteLine(branchExists
            ? $"Worktree for the existing branch {branch} at {path}."
            : $"Worktree at {path}, branch {branch} from {baseBranch}.");
        run.Ctx.Err.WriteLine($"Work there (cd {path}); when the task is done run 'deming merge' in it, then 'deming wt-remove {id}' from the main worktree.");
        return Task.FromResult(ExitCodes.Success);
    }

    /// <summary>The entries of .worktreeinclude: one relative path per line, '#' starts a comment.</summary>
    public static IReadOnlyList<string> ReadIncludes(string root)
    {
        var file = Path.Combine(root, IncludeFile);
        if (!File.Exists(file))
            return [];
        var entries = new List<string>();
        var rootFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        foreach (var raw in File.ReadAllLines(file, Encoding.UTF8))
        {
            var line = raw.Trim().TrimStart('﻿');
            if (line.Length == 0 || line.StartsWith('#'))
                continue;
            var full = Path.GetFullPath(Path.Combine(root, line));
            if (Path.IsPathRooted(line) || !full.StartsWith(rootFull, PathComparison))
                throw new CliException($"{IncludeFile}: '{line}' is not a path inside the repository. No worktree was created.");
            entries.Add(Path.GetRelativePath(root, full));
        }
        return entries;
    }

    private static void Copy(string source, string target)
    {
        if (File.Exists(source))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(source, target, overwrite: true);
            return;
        }
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            Copy(file, Path.Combine(target, Path.GetRelativePath(source, file)));
    }

    /// <summary>
    /// Writes the pre-push hook into the repository's hooks directory (git rev-parse --git-path hooks, so it follows
    /// core.hooksPath; one directory for every worktree). A pre-push that is not Deming's is never overwritten.
    /// Returns "installed", "updated" or "unchanged".
    /// </summary>
    public static string InstallHook(Git git, string mainRoot)
    {
        var dir = git.Need(mainRoot, "find the hooks directory", "rev-parse", "--path-format=absolute", "--git-path", "hooks");
        dir = Path.GetFullPath(dir, mainRoot);
        var file = Path.Combine(dir, HookName);
        string status;
        if (File.Exists(file))
        {
            var current = File.ReadAllText(file, Encoding.UTF8);
            if (!current.Contains(HookMarker, StringComparison.Ordinal))
                throw new CliException($"{file} is a pre-push hook of the project's own; deming does not overwrite it. "
                    + $"Rename it to {LocalHookName} (the Deming hook runs it in the main worktree), then run 'deming wt' again. No worktree was created.");
            if (current == HookScript)
                return "unchanged";
            status = "updated";
        }
        else
            status = "installed";

        Directory.CreateDirectory(dir);
        File.WriteAllText(file, HookScript, new UTF8Encoding(false));
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        return status;
    }

    // ---- deming merge [<id>]

    public static Task<int> Merge(CliRun run)
    {
        var git = new Git(run.Ctx);
        var repo = Repo.Open(git, run.Ctx.CurrentDirectory);
        var main = repo.Main;
        var mainBranch = main.RequireBranch();

        WorkTree tree;
        if (run.Args.Positional.Count > 0)
        {
            var branch = BranchName(run.RequireId());
            tree = repo.Trees.FirstOrDefault(t => t.Branch == branch)
                ?? throw new CliException($"No worktree has branch {branch} ('git worktree list' shows them).");
        }
        else
        {
            tree = repo.Trees.First(t => SamePath(t.Path, repo.Here));
            if (tree == main)
            {
                // A single-session workflow merges nothing: the main worktree is already the main branch.
                run.Ctx.Out.WriteLine(new JsonObject { ["merged"] = false, ["main"] = mainBranch, ["worktree"] = main.Path }.ToJsonString());
                run.Ctx.Err.WriteLine($"Nothing to merge: this is the main worktree ({mainBranch}). Run it inside a task worktree, or give the task id.");
                return Task.FromResult(ExitCodes.Success);
            }
        }

        var taskBranch = tree.Branch
            ?? throw new CliException($"The worktree {tree.Path} is on a detached HEAD; check out its task branch first. Nothing was changed.");
        if (taskBranch == mainBranch)
            throw new CliException($"The worktree {tree.Path} is on {mainBranch} itself; nothing to merge.");

        var dirty = git.Need(tree.Path, "read the worktree status", "status", "--porcelain", "--untracked-files=no");
        if (dirty.Length > 0)
            throw new CliException($"{tree.Path} has uncommitted changes; commit them (or discard them) first. Nothing was changed.");

        var before = git.Need(tree.Path, "read the branch", "rev-parse", "HEAD");
        var rebase = git.Run(tree.Path, "rebase", mainBranch);
        if (!rebase.Ok)
        {
            var conflicts = git.Run(tree.Path, "diff", "--name-only", "--diff-filter=U").Out
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            git.Run(tree.Path, "rebase", "--abort");
            throw new CliException(conflicts.Length > 0
                ? $"Rebasing {taskBranch} onto {mainBranch} stops on a conflict in: {string.Join(", ", conflicts)}. "
                    + $"The rebase was aborted and nothing was changed. Resolve it in {tree.Path} (git rebase {mainBranch}, fix the files, git rebase --continue), then run 'deming merge' again."
                : $"Rebasing {taskBranch} onto {mainBranch} failed: {rebase.Message}. The rebase was aborted and nothing was changed.");
        }
        var after = git.Need(tree.Path, "read the branch", "rev-parse", "HEAD");

        var ff = git.Run(main.Path, "merge", "--ff-only", taskBranch);
        if (!ff.Ok)
            throw new CliException($"{taskBranch} is rebased onto {mainBranch}, but {mainBranch} could not be fast-forwarded in {main.Path}: {ff.Message}. "
                + $"{mainBranch} was not moved; fix that and run 'deming merge' again.");

        run.Ctx.Out.WriteLine(new JsonObject
        {
            ["merged"] = true,
            ["branch"] = taskBranch,
            ["main"] = mainBranch,
            ["commit"] = after,
            ["rebased"] = before != after,
            ["worktree"] = tree.Path,
        }.ToJsonString());
        run.Ctx.Err.WriteLine($"{mainBranch} is now at {after[..Math.Min(after.Length, 12)]} ({taskBranch}). Push from {main.Path}; remove the worktree with 'deming wt-remove'.");
        return Task.FromResult(ExitCodes.Success);
    }

    // ---- deming wt-remove <id>

    public static Task<int> Remove(CliRun run)
    {
        var id = run.RequireId();
        var git = new Git(run.Ctx);
        var repo = Repo.Open(git, run.Ctx.CurrentDirectory);
        var main = repo.Main;
        var mainBranch = main.RequireBranch();
        var branch = BranchName(id);

        var tree = repo.Trees.FirstOrDefault(t => t.Branch == branch);
        var branchExists = git.Run(main.Path, "rev-parse", "--verify", "--quiet", "refs/heads/" + branch).Ok;
        if (tree is null && !branchExists)
            throw new CliException($"There is no worktree and no branch {branch}.");
        if (tree == main)
            throw new CliException($"{branch} is checked out in the main worktree; it is not removed.");
        if (branchExists && !git.Run(main.Path, "merge-base", "--is-ancestor", branch, mainBranch).Ok)
            throw new CliException($"{branch} is not merged into {mainBranch}: run 'deming merge {id}' first. Nothing was removed.");

        if (tree is not null)
        {
            if (IsInside(run.Ctx.CurrentDirectory, tree.Path))
                throw new CliException($"You are inside {tree.Path}; run 'deming wt-remove {id}' from the main worktree. Nothing was removed.");
            var dropped = DropCopies(git, main.Path, tree.Path);
            var removed = git.Run(main.Path, "worktree", "remove", tree.Path);
            if (!removed.Ok)
            {
                foreach (var (copy, source) in dropped)
                    File.Copy(source, copy, overwrite: true);
                throw new CliException($"git refuses to remove {tree.Path}: {removed.Message}. Commit or discard what is left there; nothing was removed.");
            }
            var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(tree.Path));
            if (parent is not null && Directory.Exists(parent) && !Directory.EnumerateFileSystemEntries(parent).Any()
                && Path.GetFileName(parent).EndsWith("-wt", StringComparison.Ordinal))
                Directory.Delete(parent);
        }
        if (branchExists)
            git.Need(main.Path, "delete the branch", "branch", "-d", branch);

        run.Ctx.Out.WriteLine(new JsonObject
        {
            ["branch"] = branch,
            ["worktree"] = tree?.Path,
            ["removedWorktree"] = tree is not null,
            ["deletedBranch"] = branchExists,
        }.ToJsonString());
        run.Ctx.Err.WriteLine(tree is null ? $"Deleted branch {branch}." : $"Removed {tree.Path} and branch {branch}.");
        return Task.FromResult(ExitCodes.Success);
    }

    /// <summary>
    /// 'deming wt' copied .deming and the .worktreeinclude files in; an untracked copy that still matches the main
    /// worktree's would keep 'git worktree remove' from removing the tree, so it goes first (and comes back when the
    /// removal fails). A changed one stays. Returns the dropped copies with their sources.
    /// </summary>
    private static List<(string Copy, string Source)> DropCopies(Git git, string mainRoot, string treeRoot)
    {
        var dropped = new List<(string, string)>();
        var entries = new List<string>(ReadIncludes(mainRoot)) { ".deming" };
        foreach (var entry in entries)
        {
            var copy = Path.Combine(treeRoot, entry);
            var source = Path.Combine(mainRoot, entry);
            if (!File.Exists(copy) || !File.Exists(source))
                continue;
            if (git.Run(treeRoot, "ls-files", "--error-unmatch", "--", entry).Ok)
                continue;
            if (File.ReadAllBytes(copy).AsSpan().SequenceEqual(File.ReadAllBytes(source)))
            {
                File.Delete(copy);
                dropped.Add((copy, source));
            }
        }
        return dropped;
    }

    // ---- paths

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static string Normal(string path) => Path.TrimEndingDirectorySeparator(Physical(Path.GetFullPath(path)));

    /// <summary>
    /// The path with every symbolic link on the way resolved, as far as it exists. git reports physical paths
    /// while the current directory may be a logical one (macOS: /var is a link to /private/var), and the two
    /// must compare equal.
    /// </summary>
    private static string Physical(string fullPath, int depth = 0)
    {
        var root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrEmpty(root) || depth > 16)
            return fullPath;

        var current = root;
        foreach (var part in fullPath[root.Length..].Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            try
            {
                var info = new DirectoryInfo(current);
                if (info.Exists && info.LinkTarget is not null && info.ResolveLinkTarget(returnFinalTarget: true) is { } target)
                    current = Physical(target.FullName, depth + 1); // the target itself may go through a link
            }
            catch (IOException)
            {
                // Not resolvable (a broken link, no access): keep the logical path.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
        return current;
    }

    public static bool SamePath(string a, string b) => string.Equals(Normal(a), Normal(b), PathComparison);

    private static bool IsInside(string path, string root) =>
        SamePath(path, root) || Normal(path).StartsWith(Normal(root) + Path.DirectorySeparatorChar, PathComparison);

    // ---- git

    /// <summary>A worktree as 'git worktree list --porcelain' reports it; Branch is null on a detached HEAD.</summary>
    internal sealed record WorkTree(string Path, string? Branch, bool Bare)
    {
        public string RequireBranch() => Branch
            ?? throw new CliException($"The main worktree {Path} is on a detached HEAD; check out the main branch there first.");
    }

    /// <summary>The repository as seen from a directory: its worktrees, the main one first.</summary>
    internal sealed record Repo(string Here, IReadOnlyList<WorkTree> Trees)
    {
        public WorkTree Main => Trees[0];

        public static Repo Open(Git git, string from)
        {
            var top = git.Run(from, "rev-parse", "--show-toplevel");
            if (!top.Ok)
                throw new CliException($"{from} is not inside a git repository (or work tree). Run it in the repository.");
            var here = Path.GetFullPath(top.Out.Trim());

            var trees = new List<WorkTree>();
            string? path = null, branch = null;
            var bare = false;
            foreach (var raw in (git.Need(here, "list the worktrees", "worktree", "list", "--porcelain") + "\n").Split('\n'))
            {
                var line = raw.TrimEnd('\r');
                if (line.StartsWith("worktree ", StringComparison.Ordinal))
                    path = Path.GetFullPath(line["worktree ".Length..]);
                else if (line.StartsWith("branch refs/heads/", StringComparison.Ordinal))
                    branch = line["branch refs/heads/".Length..];
                else if (line == "bare")
                    bare = true;
                else if (line.Length == 0 && path is not null)
                {
                    trees.Add(new WorkTree(path, branch, bare));
                    (path, branch, bare) = (null, null, false);
                }
            }
            if (trees.Count == 0 || trees[0].Bare)
                throw new CliException("This repository has no main worktree (it is bare); deming wt needs one.");
            return new Repo(here, trees);
        }
    }
}

internal sealed record GitResult(int ExitCode, string Out, string Err)
{
    public bool Ok => ExitCode == 0;

    /// <summary>git's own words, for an error message.</summary>
    public string Message
    {
        get
        {
            var text = (Err.Trim().Length > 0 ? Err : Out).Trim();
            return text.Length > 0 ? text.ReplaceLineEndings(" ") : $"git exited with {ExitCode}";
        }
    }
}

/// <summary>Runs git as a child process: argument list (no shell), UTF-8, no terminal prompts.</summary>
internal sealed class Git(CliContext ctx)
{
    public GitResult Run(string workingDirectory, params string[] args)
    {
        var start = new ProcessStartInfo(ctx.GitExecutable ?? "git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in args)
            start.ArgumentList.Add(arg);
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        if (ctx.GitEnvironment is { } extra)
            foreach (var (name, value) in extra)
            {
                if (value is null)
                    start.Environment.Remove(name);
                else
                    start.Environment[name] = value;
            }

        Process process;
        try
        {
            process = Process.Start(start) ?? throw new CliException("git could not be started.");
        }
        catch (Win32Exception)
        {
            throw new CliException("git was not found. Install git and make sure 'git' is on PATH.");
        }
        using (process)
        {
            process.StandardInput.Close();
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            process.WaitForExit();
            return new GitResult(process.ExitCode, stdout.GetAwaiter().GetResult(), stderr.GetAwaiter().GetResult());
        }
    }

    /// <summary>Runs git and returns its trimmed output; a failure is a CliException that says what was being done.</summary>
    public string Need(string workingDirectory, string doing, params string[] args)
    {
        var result = Run(workingDirectory, args);
        if (!result.Ok)
            throw new CliException($"git could not {doing}: {result.Message}");
        return result.Out.Trim();
    }
}
