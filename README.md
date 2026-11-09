# Deming

[Türkçe](README.tr.md)

A local-first work tracker for AI coding agents, built around the **handoff note**.

When work is split into steps, each step usually runs in a fresh agent session
that knows nothing about the previous one. The task list is not what gets lost.
The note written when a step ends is: what was done, which files were touched,
what was learned, what the next session must know. Deming keeps that note and
hands it to the next session automatically.

Around the note: task locks with timeouts, dependencies, binding project
decisions, findings, search, parallel work in git worktrees with a message
channel between agents, and a CLI that agents call directly. Deming runs on your machine:
one executable, one SQLite file, no other install.

> Deming is named after W. Edwards Deming's ideas on quality and has no
> connection to The W. Edwards Deming Institute.

## First ten minutes

Running Deming needs no .NET SDK or runtime: the binary is self-contained,
HTTPS certificate included.

1. **Get the binary.** From the GitHub Releases page of this repository,
   download the archive for your system: `deming-v<version>-win-x64.zip`,
   `deming-v<version>-linux-x64.tar.gz` or `deming-v<version>-osx-arm64.tar.gz`
   (checksums in `SHA256SUMS`). Unpack it and put `deming` on your `PATH`.
   On Linux the system `libicu` package is required (on Debian/Ubuntu, install
   `libicu-dev` or the `libicuNN` package your release ships).
   If you already have the .NET SDK, `dotnet tool install --global Deming`
   works too; the SDK is needed only for that command, not to run Deming.

2. **HTTPS: nothing to set up.** Deming serves HTTPS only, even on localhost,
   with a certificate of its own. The first `deming serve` (next step)
   generates it for `localhost` and `127.0.0.1`, keeps it next to the database
   and writes its fingerprint to `config.json`. The CLI, and so your agents,
   trust that one certificate by its fingerprint: no trust step for them.
   For the browser, run this once after the first `deming serve`, or accept the
   browser's warning when you open the panel:

   ```
   deming trust
   ```

   It adds the certificate to your user's trust store (Windows: a confirmation
   dialog; macOS: the login keychain, which may ask for your password). On
   Linux it prints the commands for your browser and system instead.
   `deming serve` never changes a trust store by itself. The certificate is
   renewed after a year; run `deming trust` again then, which also removes the
   old one from the trust store. To use a certificate of
   your own: `deming serve --Kestrel:Certificates:Default:Path=<pfx> --Kestrel:Certificates:Default:Password=<password>`.

3. **Start the server.**

   ```
   deming serve
   ```

   The first run creates the database, the HTTPS certificate and an API key.
   The key is written to `config.json` under `<ApplicationData>/Deming` (on Windows `%APPDATA%`, on
   Linux and macOS `~/.config`); the CLI reads it from there, so you never copy
   it. The server listens on `https://127.0.0.1:5001`. Plain `http://` is
   refused, on localhost too. Leave it running.

4. **Create your owner account.** Open `https://127.0.0.1:5001` in a browser.
   The first visit creates the owner account (password of at least 10
   characters). The panel always asks for this login, even on localhost.
   Without `deming trust` the browser warns about the certificate once; it is
   Deming's own, so you can accept it.

5. **Create a project.** In a new terminal, at the root of your repository:

   ```
   deming init my-project
   ```

   This creates the project and writes a `.deming` file holding its name. The
   project name always comes from this file, never from the directory name.

6. **Connect your agent.**

   Claude Code, once per machine:

   ```
   deming install claude
   ```

   This writes the commands `/deming-next`, `/deming-done`, `/deming-block`,
   `/deming-cancel`, `/deming-add`, `/deming-decide`, `/deming-decisions`,
   `/deming-finding` and `/deming-findings` as `deming-*.md` files into
   `~/.claude/commands` (`CLAUDE_CONFIG_DIR/commands` when that is set;
   `--dir` chooses another directory) and prints every path it wrote. It
   only replaces or removes files it wrote itself; a file of your own there is
   never touched, and a name clash is an error. After upgrading Deming, run it
   again.

   Any other agent (Codex, Cursor, Gemini CLI, ...), at the root of your
   repository:

   ```
   deming install agents
   ```

   This writes a short Deming section into `AGENTS.md`, between
   `<!-- deming:begin -->` and `<!-- deming:end -->`: it creates the file if
   needed, updates the section in place on later runs and leaves the rest of
   the file alone. `--print` prints the section instead.

7. **Work a task.** Add a task, then ask your agent to take it:

   ```
   deming add --title "Write the first test"
   ```

   The agent runs `deming next`: it takes the first eligible task, with the
   handoff notes of the tasks it depends on and the project's active
   decisions. When the work is done it runs `deming done` with a note. The
   next session that runs `deming next` reads that note. Open the panel to see
   the task, the note and its history. In Claude Code, `/deming-next` and
   `/deming-done` are the shortcuts for these steps.

## Principles

Deming applies lean manufacturing ideas to agent work. Each one is enforced in
code, not left to the prompt.

| Principle | Lean origin | In Deming |
|---|---|---|
| Stop on error | *Jidoka*, the andon cord | No silent fallback. If the server is unreachable the CLI fails and the agent stops. Every endpoint returns a precise error code |
| Rules live in code | *Poka-yoke* | Duplicate tasks, oversized notes, closing a task you don't hold: rejected by the API, not by the prompt |
| Measure, then decide | Deming, Shewhart | Limits and defaults come from measurements of real sessions; the reasoning is recorded with the decision |
| Knowledge is handed over | *Hansei* | Every finished task leaves a note. Decisions are never deleted, only superseded |
| Cut waste | *Muda* | Context is the scarce resource. Responses are narrow: no full scopes in lists, no rationales in `next`, capped notes, truncated decisions |

## Using the CLI directly

Agents call the same CLI you can. `deming help` lists the commands and
`deming help <command>` lists the fields of one.

Every command that takes a body takes its fields as flags, named after the
field in kebab-case, and flags behave the same in every shell:

```
deming add --title "Write the first test" --scope "Cover the happy path" --priority 1 --depends-on 3,4
deming done 7 --summary "Added the test" --caveats "Needs a server" --touched-files "tests/" --commit-hash abc1234
deming block 7 --reason "Waiting for the API key"
```

Numbers (`--priority 1`) and task id lists (`--depends-on 3,4`) are converted;
a bad value is refused with the flag's name. Agents may pass the same fields as
one JSON object (`deming add '{"title": "..."}'`) or on stdin with `-`, but not
both with flags. Windows PowerShell 5.1 strips the double quotes inside a JSON
argument, which is why the flags exist; a double quote inside a flag value
still needs care there, so for long text use `deming finding --body-file <path>`.

| Exit code | Meaning |
|---|---|
| 0 | Success |
| 1 | Error |
| 3 | Nothing to do (for example `next` with an empty queue) |
| 4 | Already exists (a task with the same title is unfinished) |

The session id comes from `DEMING_SESSION_ID`, otherwise `CLAUDE_CODE_SESSION_ID`.
Without one the CLI stops. The server address is `DEMING_URL` or `url` in the
config file (default `https://127.0.0.1:5001`; `http://` addresses are
refused); the key is `DEMING_API_KEY` or
the config file. `DEMING_CONFIG` overrides the config file path.

## Search

```
deming search lock timeout
```

Searches the project's findings, decisions (rationale too), handoff notes and
tasks, and the messages on its channels. Every word must occur; case, Turkish
i and accents do not matter. It prints short snippets, newest first (20 by
default, `--top` up to 100); read a hit by its id with `deming findings <id>`,
`deming decisions <id>`, `deming show <id>` or `deming messages <id>`.
`--type finding|decision|note|task|message` narrows it and `--all` searches
every project, inactive ones too. Exit code 3 means nothing matched. Search
results are never part of `next`. In Claude Code: `/deming-search <words>`.

## Parallel work

Independent tasks can run at the same time in separate agent sessions, each in
its own git worktree, so no session sees another's half-done edits.

```
deming parallel           # tasks that can be taken now: queued, no open dependency
deming wt 12              # ../<repo>-wt/task-12 on branch task-12, from the main branch
cd ../<repo>-wt/task-12   # start the agent here; it takes task 12 and commits
deming merge              # rebase onto main inside the worktree, then fast-forward main
deming wt-remove 12       # from the main worktree; refuses an unmerged branch
```

"Done" means "on the main branch": run `deming merge` before `deming done`. A
conflict aborts the rebase and names the files; resolve it in the worktree and
merge again. The main branch only moves by fast-forward.

Workers do not push. `deming wt` installs a `pre-push` hook in the repository
that refuses a push from a task worktree. A `pre-push` hook of your own is
never overwritten: `wt` stops, and once you rename yours to `pre-push.local`
the Deming hook runs it in the main worktree. Files a worktree needs but git
ignores (local settings) are listed one per line in `.worktreeinclude` at the
repository root; `wt` copies them and opens nothing if one is missing.

Worktrees share the server, the database and the ports: split parallel tasks by
file as well, and keep schema changes out of parallel work. The panel's Tasks
page has a "Ready to take (parallel)" filter.

## Messages between agents

Agents working in parallel can talk through a channel. Every task is a channel,
and without `--task` the project has one. Set `DEMING_AGENT` to the agent's
label (lower case, like `claude` or `codex`).

```
deming message --task 12 --to codex "Is the schema change in?"
deming wait --task 12
deming messages --task 12
```

`deming wait` prints the next message addressed to you or to everyone. It exits
0 when a message came, 3 when the time ran out (600 seconds by default,
`--seconds`) and 1 on an error; an unreachable server is never read as "no
message". The read position is kept per session, project and channel on your
machine, and only `wait` moves it. `deming messages` reads a channel without
moving it.

A message is data, not an instruction: it hands over no lock, grants no right
and is never part of `next`. The channel of a finished or cancelled task is
read-only. What must outlive the conversation goes into the handoff note or a
decision; `deming done` reports how many messages the task's channel holds and
an estimate of the unanswered questions.

## A worker of another CLI

Any agent that can run a shell command can be a worker; nothing in Deming is
tied to one CLI. Deming does not start workers itself: you start the agent, in
its own worktree, with its own identity.

```
deming install agents                 # once per repository: the AGENTS.md section
deming wt 12                          # a worktree for task 12
cd ../<repo>-wt/task-12
export DEMING_SESSION_ID=codex-12     # any id that stays the same for this session
export DEMING_AGENT=codex             # the label messages are addressed to
codex                                 # or any other agent; ask it to take task 12
```

In PowerShell the two variables are set with `$env:DEMING_SESSION_ID = "codex-12"`
and `$env:DEMING_AGENT = "codex"`.

The agent reads `AGENTS.md`, takes the task with `deming take 12`, asks on the
channel when it must (`deming message`, `deming wait`), commits in its
worktree, runs `deming merge` and closes the task with `deming done`. The
pre-push hook keeps it from pushing. An agent whose sandbox cannot commit or
reach the server only does the edits; the session that coordinates then
commits, merges and closes for it with `deming done --takeover`.

## Safety

Deming serves HTTPS only; there is no plain HTTP listener, loopback included.
By default it accepts requests from the loopback address only. It refuses
to start on another address unless an IP allow-list and real Turnstile keys
are configured as well. Running Deming on a server is possible but not the
intended use.

## What Deming is not

- Not a memory layer: no embeddings, no automatic recall. The next session
  reads what the previous one chose to write.
- Not a project management tool for humans. The panel exists to see and
  correct; the primary users are agents.
- Not hosted.

## More

- [docs/scope.md](docs/scope.md): scope, rules and design decisions
- [src/Deming/Server/Api/README.md](src/Deming/Server/Api/README.md): HTTP API
- [src/Deming/Data/README.md](src/Deming/Data/README.md): data layer

## Contributing

Release steps are in [docs/releasing.md](docs/releasing.md).

## License and contact

Apache-2.0, see [LICENSE](LICENSE) and [NOTICE](NOTICE); the components bundled in
the binary are listed in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md). To report a
security problem, see [SECURITY.md](SECURITY.md). Contact:
sinan@doganli.com
