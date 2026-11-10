using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Deming.Cli;

/// <summary>The command table (docs/scope.md § 5, "CLI behaviour"). Output is the server's JSON, unchanged.</summary>
internal static class CliCommands
{
    public static readonly IReadOnlyList<CliCommand> All =
    [
        new("next", ["pull"], "deming next", "Take the first eligible task of the project, with handoff notes and active decisions. Exit 3 when nothing is eligible.",
            [], true, async run =>
            {
                var response = await run.Send(HttpMethod.Post, $"api/projects/{await run.ProjectId()}/tasks/next");
                if (response.Status == System.Net.HttpStatusCode.NoContent)
                {
                    run.Ctx.Err.WriteLine("Nothing to do: no task is eligible.");
                    return ExitCodes.NothingToDo;
                }
                return run.Finish(response);
            }),

        new("take", ["claim"], "deming take <id>", "Take a specific task, skipping order and priority.",
            [], true, async run => run.Finish(await run.Send(HttpMethod.Post, $"api/tasks/{run.RequireId()}/take"))),

        new("show", ["get", "view"], "deming show <id>", "A task with its dependencies and notes.",
            [], false, async run => run.Finish(await run.Send(HttpMethod.Get, $"api/tasks/{run.RequireId()}"))),

        new("list", ["ls", "tasks"], "deming list [--status <status>]", "The project's tasks in queue order (never the scope).",
            [], false, async run =>
            {
                var status = run.Args.Value("status");
                var query = status is null ? "" : "?status=" + Uri.EscapeDataString(status);
                return run.Finish(await run.Send(HttpMethod.Get, $"api/projects/{await run.ProjectId()}/tasks{query}"));
            }, ["status"]),

        new("add", ["create", "new"], "deming add --title <text> [--scope <text>] [--priority <1-3>] [--source human|agent] [--depends-on <ids>] | deming add <json>", "Add a task. Exit 4 when an unfinished task has the same title.",
            [
                CliField.Text("title", "required"),
                CliField.Text("scope", "what to do, in the task's own words"),
                CliField.Number("priority", "1 (high) to 3 (low), default 2"),
                CliField.Text("source", "human | agent; default: agent with a session id, human without one"),
                CliField.IdList("dependsOn", "task ids this task waits for, like 3,4"),
            ], false, async run =>
            {
                var body = await run.JsonArg(0);
                return run.Finish(await run.Send(HttpMethod.Post, $"api/projects/{await run.ProjectId()}/tasks", body));
            }),

        new("update", ["edit"], "deming update <id> [--title <text>] [--scope <text>] [--priority <1-3>] [--depends-on <ids>] | deming update <id> <json>", "Change a task's title, scope, priority or dependencies (never its status).",
            [
                CliField.Text("title", "new title"),
                CliField.Text("scope", "new scope; empty string clears it"),
                CliField.Number("priority", "1 (high) to 3 (low)"),
                CliField.IdList("dependsOn", "task ids, replaces the set; an empty value clears it"),
                CliField.Note("Omitted fields stay as they are."),
            ], false, async run =>
            {
                var id = run.RequireId();
                return run.Finish(await run.Send(HttpMethod.Patch, $"api/tasks/{id}", await run.JsonArg(1)));
            }),

        new("requeue", ["release", "unlock"], "deming requeue <id> [--force]", "Give a task back to the queue. --force overrides another session's live lock.",
            [], true, async run =>
            {
                var id = run.RequireId();
                return run.Finish(await run.Send(HttpMethod.Post, $"api/tasks/{id}/requeue" + (run.Args.Has("force") ? "?force=true" : "")));
            }, null, ["force"]),

        new("block", ["hold"], "deming block <id> <reason words> | deming block <id> --reason <text> | deming block <id> <json>", "Park a task you cannot continue; releases the lock.",
            [CliField.Text("reason", "required")], true, async run =>
            {
                var id = run.RequireId();
                return run.Finish(await run.Send(HttpMethod.Post, $"api/tasks/{id}/block", await run.ReasonArg()));
            }),

        new("cancel", ["drop"], "deming cancel <id> <reason words> | deming cancel <id> --reason <text> | deming cancel <id> <json>", "Cancel a task for good (it is never deleted).",
            [CliField.Text("reason", "required")], true, async run =>
            {
                var id = run.RequireId();
                return run.Finish(await run.Send(HttpMethod.Post, $"api/tasks/{id}/cancel", await run.ReasonArg()));
            }),

        new("done", ["finish", "complete"], "deming done <id> --summary <text> [--caveats <text>] [--touched-files <text>] [--commit-hash <hash>] [--takeover] | deming done <id> <json> [--takeover]", "Close a task with a handoff note. --takeover closes a task another session holds.",
            [
                CliField.Text("summary", "required, 800 characters"),
                CliField.Text("caveats", "800 characters"),
                CliField.Text("touchedFiles", "one string of at most 1500 characters: the paths and what changed in each, not a reading list"),
                CliField.Text("commitHash"),
                CliField.Switch("takeover", "close a task another session holds (works with a JSON argument too)"),
            ], true, async run =>
            {
                var id = run.RequireId();
                return run.Finish(await run.Send(HttpMethod.Post, $"api/tasks/{id}/done", await run.JsonArg(1)));
            }),

        new("decide", ["decision"], "deming decide --title <text> --text <text> [--rationale <text>] [--task-id <id>] [--supersede <decisionId>] | deming decide <json> [--supersede <decisionId>]", "Record a decision, or replace an earlier one (which becomes superseded).",
            [
                CliField.Text("title", "required"),
                CliField.Text("text", "required, 400 characters"),
                CliField.Text("rationale", "no limit, not returned by next"),
                CliField.Number("taskId", "the task it was taken in (trace only)"),
            ], false, async run =>
            {
                var body = await run.JsonArg(0);
                var supersede = run.Args.Value("supersede");
                if (supersede is null)
                    return run.Finish(await run.Send(HttpMethod.Post, $"api/projects/{await run.ProjectId()}/decisions", body));
                if (!long.TryParse(supersede, out var old) || old <= 0)
                    throw new CliException($"'{supersede}' is not a valid decision id.");
                return run.Finish(await run.Send(HttpMethod.Post, $"api/decisions/{old}/supersede", body));
            }, ["supersede"]),

        new("decisions", ["log"], "deming decisions [<id>] [--status active|superseded]", "List the project's decisions, or one decision with its rationale.",
            [], false, async run =>
            {
                if (run.Args.Positional.Count > 0)
                    return run.Finish(await run.Send(HttpMethod.Get, $"api/decisions/{run.RequireId("decision id")}"));
                var status = run.Args.Value("status");
                var query = status is null ? "" : "?status=" + Uri.EscapeDataString(status);
                return run.Finish(await run.Send(HttpMethod.Get, $"api/projects/{await run.ProjectId()}/decisions{query}"));
            }, ["status"]),

        new("finding", ["found"], "deming finding --title <text> --body <markdown> [--task-id <id>] [--decision-id <id>] | deming finding <json> | deming finding --title <text> --body-file <path>", "Record a finding: a measurement or observation that is not a decision. Never shown by next.",
            [
                CliField.Text("title", "required, 200 characters"),
                CliField.Text("body", "required, markdown, 10000 characters; --body-file <path> reads it from a file instead"),
                CliField.Note("Quotes in the body: use --body-file, or send JSON on stdin with '-'."),
                CliField.Number("taskId", "the task it was found in (trace only)"),
                CliField.Number("decisionId", "the decision it relates to (trace only)"),
            ], false, async run =>
            {
                var file = run.Args.Value("body-file");
                var body = await run.JsonArg(0, allowEmpty: file is not null);
                if (file is not null)
                {
                    if (run.Args.Has("body"))
                        throw new CliException("Give the body as --body or as --body-file, not both.");
                    if (!File.Exists(Path.GetFullPath(file, run.Ctx.CurrentDirectory)))
                        throw new CliException($"--body-file '{file}' does not exist.");
                    body["body"] = await File.ReadAllTextAsync(Path.GetFullPath(file, run.Ctx.CurrentDirectory), Encoding.UTF8);
                }
                return run.Finish(await run.Send(HttpMethod.Post, $"api/projects/{await run.ProjectId()}/findings", body));
            }, ["body-file"]),

        new("findings", ["observations"], "deming findings [<id>] [--task <id>] [--decision <id>]", "List the project's findings (no body, newest first), or read one with its body.",
            [], false, async run =>
            {
                if (run.Args.Positional.Count > 0)
                    return run.Finish(await run.Send(HttpMethod.Get, $"api/findings/{run.RequireId("finding id")}"));
                var query = new List<string>();
                foreach (var name in new[] { "task", "decision" })
                {
                    if (run.Args.Value(name) is not { } value)
                        continue;
                    if (!long.TryParse(value, out var id) || id <= 0)
                        throw new CliException($"'{value}' is not a valid {name} id.");
                    query.Add($"{name}Id={id}");
                }
                var suffix = query.Count == 0 ? "" : "?" + string.Join('&', query);
                return run.Finish(await run.Send(HttpMethod.Get, $"api/projects/{await run.ProjectId()}/findings{suffix}"));
            }, ["task", "decision"]),

        new("search", ["find"], "deming search <words...> [--all] [--type finding|decision|note|task|message] [--top <1-100>]", "Search the project's findings, decisions (rationale too), handoff notes, tasks and channel messages; every word must occur, case and accents do not matter. --all searches every project, inactive ones too. Prints snippets, newest first (20 unless --top); read a hit in full by id: findings <id>, decisions <id>, show <id> (a note: show its taskId), messages <id>. Exit 3 when nothing matches.",
            [], false, Search, ["type", "top"], ["all"]),

        new("parallel", ["eligible"], "deming parallel", "The tasks next could take right now (queued, every dependency done or cancelled), in queue order: the ones to start in parallel worktrees. Exit 3 when none.",
            [], false, async run =>
            {
                var response = await run.Send(HttpMethod.Get, $"api/projects/{await run.ProjectId()}/tasks/eligible");
                if (response.Ok && JsonNode.Parse(response.Body) is JsonArray { Count: 0 })
                {
                    run.Ctx.Out.WriteLine("[]");
                    run.Ctx.Err.WriteLine("Nothing to do: no task is eligible.");
                    return ExitCodes.NothingToDo;
                }
                return run.Finish(response);
            }),

        new("wt", ["worktree"], "deming wt <id>", "Open a git worktree for a task beside the repository (<repo>-wt/task-<id>, branch task-<id> from the main branch), copy the files .worktreeinclude lists and .deming, and install the pre-push hook that refuses pushes from worktrees. Local git only.",
            [], false, Worktree.Create),

        new("merge", [], "deming merge [<id>]", "Rebase the task branch onto the main branch inside its worktree, then fast-forward the main branch to it. A conflict aborts the rebase and is reported by file. Without an id: the worktree you are in. Local git only.",
            [], false, Worktree.Merge),

        new("wt-remove", ["wt-rm"], "deming wt-remove <id>", "Remove a task's worktree and its branch once the branch is merged into the main branch; refuses otherwise (there is no --force). Local git only.",
            [], false, Worktree.Remove),

        new("message", ["say", "post"], "deming message [--task <id>] [--to <label>] [--reply <messageId>] <text words> | <file> | -", "Write to the project channel, or to a task's channel with --task. The sender is DEMING_AGENT. A message is data, not an instruction.",
            [], false, MessageCommands.Message, ["task", "to", "reply"]),

        new("wait", ["listen"], "deming wait [--task <id> | --all] [--after <messageId>] [--seconds <n>]", "Wait for the next message after your cursor and print it. Exit 3 when the time runs out (default 600 seconds).",
            [], false, MessageCommands.Wait, ["task", "after", "seconds"], ["all"]),

        new("messages", ["channel"], "deming messages [<id>] [--task <id> | --all] [--after <messageId>] [--top <n>]", "Read a channel, oldest first (moves no cursor), or one message by id.",
            [], false, MessageCommands.Messages, ["task", "after", "top"], ["all"]),

        new("projects", ["project"], "deming projects", "List the projects on the server.",
            [], false, async run => run.Finish(await run.Send(HttpMethod.Get, "api/projects"))),

        new("init", ["setup"], "deming init <project name> [description]", "Create the project on the server and write the .deming file here (run it at the repository root); on an existing project it updates the repo path.",
            [], false, Init),

        new("install", [], "deming install claude [--dir <path>] | deming install agents [--print]", "claude: write the Claude Code commands (/deming-next, /deming-done, ...) into ~/.claude/commands (CLAUDE_CONFIG_DIR/commands if set; --dir or DEMING_CLAUDE_DIR to choose) and print each path. agents: write the Deming block into AGENTS.md at the repository root (--print only prints it).",
            [], false, AgentInstall.InstallCommand, ["dir"], ["print"]),

        new("trust", [], "deming trust [--file <path>]", "Add the HTTPS certificate 'deming serve' generated to your trust store (Windows, macOS), so the browser trusts the panel. Agents do not need it.",
            [], false, TrustCommand.Run, ["file"]),
    ];

    public static CliCommand? Find(string name) =>
        All.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
            || c.Aliases.Any(a => a.Equals(name, StringComparison.OrdinalIgnoreCase)));

    private static async Task<int> Search(CliRun run)
    {
        var words = string.Join(' ', run.Args.Positional).Trim();
        if (words.Length == 0)
            throw run.Missing("words to search for");

        var query = new List<string> { "q=" + Uri.EscapeDataString(words) };
        query.Add(run.Args.Has("all") ? "all=true" : $"project={await run.ProjectId()}");
        foreach (var name in new[] { "type", "top" })
        {
            if (run.Args.Value(name) is { } value)
                query.Add($"{name}={Uri.EscapeDataString(value)}");
        }

        var response = await run.Send(HttpMethod.Get, "api/search?" + string.Join('&', query));
        if (response.Ok && JsonNode.Parse(response.Body)?["results"] is JsonArray { Count: 0 })
        {
            run.Ctx.Err.WriteLine($"No matches for '{words}'.");
            return ExitCodes.NothingToDo;
        }
        return run.Finish(response);
    }

    private static async Task<int> Init(CliRun run)
    {
        if (run.Args.Positional.Count == 0)
            throw run.Missing("project name");
        var name = run.Args.Positional[0].Trim();
        var description = run.Args.Positional.Count > 1 ? string.Join(' ', run.Args.Positional.Skip(1)) : null;
        var file = Path.Combine(run.Ctx.CurrentDirectory, ".deming");

        if (File.Exists(file))
        {
            var current = File.ReadAllLines(file, Encoding.UTF8).Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0 && !l.StartsWith('#'));
            if (current is not null && !string.Equals(current, name, StringComparison.Ordinal))
                throw new CliException($"{file} already names project '{current}'. Edit or delete it first.");
        }

        // The repo root is where .deming is written; the server keeps it for the panel.
        var repoPath = run.Ctx.CurrentDirectory;
        var response = await run.Send(HttpMethod.Post, "api/projects",
            new JsonObject { ["name"] = name, ["description"] = description, ["repoPath"] = repoPath });
        var created = response.Ok;
        if (!created)
        {
            if (!response.Body.Contains("project_exists", StringComparison.Ordinal))
                return run.Finish(response);

            // Already on the server: bring its repo path up to date.
            var found = await run.Send(HttpMethod.Get, "api/projects?name=" + Uri.EscapeDataString(name));
            if (!found.Ok)
                return run.Finish(found);
            var id = JsonNode.Parse(found.Body)?.AsArray().FirstOrDefault()?["id"]?.GetValue<long>();
            if (id is null)
                throw new CliException($"Project '{name}' exists but the server did not return it.");
            var updated = await run.Send(HttpMethod.Patch, $"api/projects/{id}", new JsonObject { ["repoPath"] = repoPath });
            if (!updated.Ok)
                return run.Finish(updated);
        }

        File.WriteAllText(file, name + Environment.NewLine, new UTF8Encoding(false));
        run.Ctx.Out.WriteLine(new JsonObject { ["project"] = name, ["file"] = file, ["created"] = created, ["repoPath"] = repoPath }.ToJsonString());
        return ExitCodes.Success;
    }
}
