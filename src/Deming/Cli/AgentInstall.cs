using System.Text;
using System.Text.Json.Nodes;

namespace Deming.Cli;

/// <summary>
/// 'deming install claude' and 'deming install agents' (docs/scope.md § 5-6).
/// claude: the command files shipped inside the binary (src/Deming/Agent/claude/deming-*.md) are written straight
/// into Claude Code's user command directory, where they are /deming-next, /deming-done, ... Only deming-*.md files
/// are touched. Content is compared byte for byte, never by version or date.
/// agents: a marked block (src/Deming/Agent/agents-block.md) in AGENTS.md at the repository root, for any agent.
/// </summary>
internal static class AgentInstall
{
    public const string DirVariable = "DEMING_CLAUDE_DIR";
    public const string ClaudeConfigVariable = "CLAUDE_CONFIG_DIR";
    public const string BlockBegin = "<!-- deming:begin -->";
    public const string BlockEnd = "<!-- deming:end -->";
    private const string CommandPrefix = "agent/claude/";
    private const string BlockResource = "agent/agents-block.md";
    private const string CommandPattern = "deming-*.md";

    /// <summary>The line every shipped command file carries; it tells Deming's files from the user's own deming-*.md.</summary>
    public const string CommandMarker = "<!-- deming:command -->";

    private const int MarkerSearchLines = 12;

    /// <summary>Claude Code's command directory: --dir, DEMING_CLAUDE_DIR, else CLAUDE_CONFIG_DIR/commands, else ~/.claude/commands.</summary>
    public static string ClaudeDir(Func<string, string?> env, string? explicitDir = null)
    {
        var chosen = !string.IsNullOrWhiteSpace(explicitDir) ? explicitDir : env(DirVariable);
        if (!string.IsNullOrWhiteSpace(chosen))
            return Path.GetFullPath(chosen);
        var config = env(ClaudeConfigVariable);
        if (!string.IsNullOrWhiteSpace(config))
            return Path.GetFullPath(Path.Combine(config, "commands"));
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".claude", "commands");
    }

    /// <summary>The shipped command files by file name (deming-next.md, ...).</summary>
    public static IReadOnlyDictionary<string, byte[]> Shipped()
    {
        var assembly = typeof(AgentInstall).Assembly;
        var files = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var resource in assembly.GetManifestResourceNames())
            if (resource.StartsWith(CommandPrefix, StringComparison.Ordinal))
                files[resource[CommandPrefix.Length..]] = Resource(resource);
        return files;
    }

    /// <summary>
    /// True when the file is one Deming wrote: every shipped command carries <see cref="CommandMarker"/> on a line
    /// of its own right after the front matter. A deming-*.md without it is the user's.
    /// </summary>
    public static bool IsDemingFile(string path)
    {
        try
        {
            return File.ReadLines(path, Encoding.UTF8).Take(MarkerSearchLines).Any(line => line.Trim() == CommandMarker);
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>
    /// Write the shipped commands into the directory and drop Deming's own deming-*.md files that are no longer
    /// shipped. A deming-*.md without the marker is never deleted or overwritten; a name clash with one is an
    /// error raised before anything is written.
    /// </summary>
    public static (IReadOnlyList<string> Written, IReadOnlyList<string> Removed) InstallClaude(string dir)
    {
        var shipped = Shipped();
        foreach (var name in shipped.Keys)
        {
            var path = Path.Combine(dir, name);
            if (File.Exists(path) && !IsDemingFile(path))
                throw new CliException($"{path} exists and was not written by Deming (it has no '{CommandMarker}' line). Rename or remove it, then run 'deming install claude' again. Nothing was written.");
        }

        Directory.CreateDirectory(dir);
        var written = new List<string>();
        foreach (var (name, content) in shipped)
        {
            var path = Path.Combine(dir, name);
            File.WriteAllBytes(path, content);
            written.Add(path);
        }

        var removed = new List<string>();
        foreach (var old in Directory.GetFiles(dir, CommandPattern))
            if (!shipped.ContainsKey(Path.GetFileName(old)) && IsDemingFile(old))
            {
                File.Delete(old);
                removed.Add(old);
            }
        return (written, removed);
    }

    /// <summary>
    /// Command files that are missing, differ from the shipped ones, or are no longer shipped. Empty when not
    /// installed, i.e. the directory holds no deming-*.md file written by Deming (the user's own are not counted).
    /// </summary>
    public static IReadOnlyList<string> Stale(string dir)
    {
        if (!Directory.Exists(dir))
            return [];
        var present = Directory.GetFiles(dir, CommandPattern).Where(IsDemingFile).Select(p => Path.GetFileName(p)).ToHashSet(StringComparer.Ordinal);
        if (present.Count == 0)
            return [];
        var shipped = Shipped();
        var stale = new List<string>();
        foreach (var (name, content) in shipped)
            if (!present.Contains(name) || !File.ReadAllBytes(Path.Combine(dir, name)).AsSpan().SequenceEqual(content))
                stale.Add(name);
        stale.AddRange(present.Where(name => !shipped.ContainsKey(name)).Order(StringComparer.Ordinal));
        return stale;
    }

    /// <summary>A warning for stderr when the installed commands are behind the binary; null when current or not installed.</summary>
    public static string? Warning(Func<string, string?> env)
    {
        var dir = ClaudeDir(env);
        var stale = Stale(dir);
        return stale.Count == 0
            ? null
            : $"warning: the installed Claude Code commands in {dir} differ from this deming ({stale.Count} file(s), e.g. {stale[0]}). Run 'deming install claude' to update them.";
    }

    /// <summary>The AGENTS.md block, markers included, with '\n' line ends.</summary>
    public static string Block()
    {
        var body = Encoding.UTF8.GetString(Resource(BlockResource)).Replace("\r\n", "\n").Trim('\n', '﻿');
        return $"{BlockBegin}\n{body}\n{BlockEnd}";
    }

    /// <summary>
    /// The file's text with the block put in: replaced in place between the markers, or appended when absent.
    /// Text outside the markers is kept as is. Throws when a begin marker has no end marker after it.
    /// </summary>
    public static string WithBlock(string? existing)
    {
        if (string.IsNullOrEmpty(existing))
            return Block() + "\n";
        var newline = existing.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var block = Block().Replace("\n", newline);

        var begin = existing.IndexOf(BlockBegin, StringComparison.Ordinal);
        if (begin >= 0)
        {
            var end = existing.IndexOf(BlockEnd, begin, StringComparison.Ordinal);
            if (end < 0)
                throw new CliException($"AGENTS.md has '{BlockBegin}' but no '{BlockEnd}' after it. Fix the markers by hand; nothing was written.");
            return existing[..begin] + block + existing[(end + BlockEnd.Length)..];
        }

        var trimmed = existing.TrimEnd();
        return trimmed.Length == 0
            ? block + newline
            : trimmed + newline + newline + block + newline;
    }

    /// <summary>The repository root: the nearest directory up from here holding .git (a directory or a worktree file), else .deming.</summary>
    public static string? RepositoryRoot(string from)
    {
        foreach (var marker in new[] { ".git", ".deming" })
            for (var dir = new DirectoryInfo(from); dir is not null; dir = dir.Parent)
            {
                var path = Path.Combine(dir.FullName, marker);
                if (Directory.Exists(path) || File.Exists(path))
                    return dir.FullName;
            }
        return null;
    }

    /// <summary>Write the block into AGENTS.md; returns "created", "updated" or "unchanged".</summary>
    public static string InstallAgents(string file)
    {
        if (!File.Exists(file))
        {
            File.WriteAllText(file, WithBlock(null), new UTF8Encoding(false));
            return "created";
        }
        var bytes = File.ReadAllBytes(file);
        var bom = bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble);
        var existing = new UTF8Encoding(false).GetString(bom ? bytes[3..] : bytes);
        var updated = WithBlock(existing);
        if (updated == existing)
            return "unchanged";
        File.WriteAllText(file, updated, new UTF8Encoding(bom));
        return "updated";
    }

    public static async Task<int> InstallCommand(CliRun run)
    {
        var target = run.Args.Positional.Count > 0 ? run.Args.Positional[0].ToLowerInvariant() : null;
        var code = target switch
        {
            "claude" => Claude(run),
            "agents" => Agents(run),
            _ => throw run.Missing("target (claude or agents)"),
        };
        await Task.CompletedTask;
        return code;
    }

    private static int Claude(CliRun run)
    {
        var dir = ClaudeDir(run.Ctx.Env, run.Args.Value("dir"));
        var (written, removed) = InstallClaude(dir);
        var result = new JsonObject
        {
            ["dir"] = dir,
            ["written"] = Paths(written),
            ["removed"] = Paths(removed),
        };
        run.Ctx.Out.WriteLine(result.ToJsonString());
        foreach (var path in written)
            run.Ctx.Err.WriteLine($"wrote {path}");
        foreach (var path in removed)
            run.Ctx.Err.WriteLine($"removed {path}");
        var commands = string.Join(", ", written.Select(p => "/" + Path.GetFileNameWithoutExtension(p)));
        run.Ctx.Err.WriteLine($"Claude Code commands: {commands}. Rerun 'deming install claude' after upgrading deming.");
        return ExitCodes.Success;
    }

    private static int Agents(CliRun run)
    {
        if (run.Args.Has("print"))
        {
            run.Ctx.Out.WriteLine(Block());
            return ExitCodes.Success;
        }
        var root = RepositoryRoot(run.Ctx.CurrentDirectory)
            ?? throw new CliException("Not inside a repository (no .git or .deming found here or above). Run it at the repository root, or use --print and paste the block yourself.");
        var file = Path.Combine(root, "AGENTS.md");
        var status = InstallAgents(file);
        run.Ctx.Out.WriteLine(new JsonObject { ["file"] = file, ["status"] = status }.ToJsonString());
        run.Ctx.Err.WriteLine(status == "unchanged"
            ? $"{file} already holds the current Deming block."
            : $"{(status == "created" ? "wrote" : "updated")} the Deming block in {file}");
        return ExitCodes.Success;
    }

    private static JsonArray Paths(IEnumerable<string> paths) =>
        new(paths.Select(p => (JsonNode?)JsonValue.Create(p)).ToArray());

    private static byte[] Resource(string name)
    {
        using var stream = typeof(AgentInstall).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Embedded resource '{name}' is missing.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}
