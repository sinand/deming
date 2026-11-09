# Deming — Scope

Status: draft, v0. This document defines what Deming is, what goes into each
release, and which rules are carried over from the private tool it is based on
(Harness). It is the reference for splitting the work into tasks.

## 1. What Deming is

A local-first work tracker for AI coding agents, built around the **handoff
note**. When a piece of work is split into steps, each step usually runs in a
fresh agent session that knows nothing about the previous one. The task list
is not what gets lost — the note written when a step ends is: what was done,
which files were touched, what was learned, what the next session must know.
Deming keeps that note, and hands it to the next session automatically.

Around the note: task locks with timeouts, dependencies, binding project
decisions, and a CLI that agents call directly.

### Principles

Deming applies lean manufacturing ideas to agent work. These are not slogans;
each one is enforced somewhere in the code (see § 5).

| Principle | Lean origin | In Deming |
|---|---|---|
| Stop on error | *Jidoka*, the andon cord | No silent fallback. If the server is unreachable the CLI fails and the agent stops. Every endpoint returns a precise error code instead of swallowing it |
| Rules live in code | *Poka-yoke* | A rule that is only an instruction will be broken sooner or later. Duplicate tasks, oversized notes, closing a task you don't hold — rejected by the API, not by the prompt |
| Measure, then decide | Deming, Shewhart | Limits and defaults come from measurements of real sessions, and the reasoning is recorded with the decision |
| Knowledge is handed over | *Hansei* | Every finished task leaves a note. Decisions are never deleted, only superseded |
| Cut waste | *Muda* | Context is the scarce resource. Responses are narrow by design: no full scopes in lists, no rationales in `next`, capped notes, truncated decisions |

The last principle has a second reason: Deming is meant to be usable by
developers who pay for tokens out of their own pocket.

### Non-goals

- Not a memory layer: no embeddings, no vector search, no automatic recall.
  What the next session reads is what the previous one chose to write.
- Not a general project management tool for humans. The panel exists to see
  and correct; the primary users are agents.
- Not hosted. Deming runs on the developer's machine. Running it on a server is
  possible and documented briefly, nothing more.

## 2. Releases

| Release | Contents |
|---|---|
| **v1 — core** | Projects, tasks, locks, dependencies, handoff notes, decisions, findings (measurements and observations that are not decisions; never shown by `next`), CLI, panel, agent integration (Claude Code commands and an `AGENTS.md` block) |
| **v1 — beyond one session** | Search, agent-to-agent message channel, parallel work with git worktrees |
| **Next** | Starting workers of other CLIs (Codex etc.) from Deming; translation store: shared translations across projects, filled with the user's own API key, resx export |
| **Later** | Docker image. Not planned by the author; contributions welcome |

The core is what a new user needs in the first ten minutes. Search, messaging
and parallel work only make sense once the single-session loop is familiar;
the README introduces them after it.

## 3. First ten minutes (acceptance criterion for v1)

On a clean machine with no .NET SDK installed, a developer who has never seen
Deming must reach a completed task with a handoff note in **under ten
minutes**, following only the README:

1. Download one file from GitHub Releases (or `dotnet tool install`).
2. `deming serve` — creates the database, an API key and its own HTTPS certificate on first run, prints the panel URL. No trust step is needed for the CLI; `deming trust` (optional) makes the browser trust the panel.
3. `deming init` in their repository — picks or creates the project, writes `.deming` and records the repository path on the server (shown in the panel; an existing project gets its path refreshed).
4. Connect the agent: `deming install claude` (Claude Code: one command, the slash commands are usable at once) or `deming install agents` (any other agent: an `AGENTS.md` block). The first `deming serve` names both; it writes to no agent's directory itself.
5. In the agent: add a task, take it, finish it with a note.

This is measured, not assumed: before the v1 release, someone outside the
project runs it on a clean machine and the time and every stumble are recorded.

## 4. Architecture

```
deming (single executable, .NET 10, self-contained)
  deming serve        ASP.NET Core: Blazor Server panel + minimal API
  deming <command>    CLI client: talks to the API over HTTP
SQLite (WAL mode)     one file under the user's data directory
```

- **One binary, two roles.** The CLI is a subcommand of the same executable,
  not a shell script. Harness's CLI needed bash, curl and Python; on Windows
  that meant Git Bash, and one agent sandbox could not run it at all.
- **SQLite (WAL mode).** No external database, no separate install.
- **The database does the work.** SQLite has no stored procedures, so their
  role is taken by named `.sql` files embedded in the binary and called by
  name. Rules and atomic operations stay in SQL: taking a lock is a single
  `UPDATE … RETURNING`, the dependency gate is a `NOT EXISTS`, cycle detection
  is a recursive CTE, and invariants such as "a lock owner exists only while
  the task is in progress" are `CHECK` constraints. C# opens the transaction
  and calls the queries; multi-statement changes use `BEGIN IMMEDIATE`.
- **Data access: no ORM.** A small layer over `Microsoft.Data.Sqlite`:
  `GetModelAsync<T>` / `GetModelListAsync<T>` / execute, mapping by attribute,
  no hand-written mapping code. Every response shape that matters for token
  budgets (`next`, list) is visible in its `.sql` file.
- **Schema** is a numbered series of `.sql` files with a `schema_version`
  table; `deming serve` applies the missing ones at startup.
- **Configuration: no `appsettings.json`.** A single-file binary must not drop
  files next to itself. Defaults live in code; user configuration is read from
  the user's config directory.
- **Tests:** xunit.v3 on Microsoft Testing Platform; `dotnet test` is enough.
  The SDK is pinned to 10.0.x in `global.json`.
- **Text matching.** SQLite's `LIKE` only folds ASCII case. Duplicate-title
  detection and search use a normalized column (diacritics removed, Turkish
  dotted/dotless i folded, lower case) computed in C#. Harness measured why
  this matters: "DRIFT" did not match "drift" and "kopru" did not match "köprü"
  under a Turkish collation.
- **English** for code, API, database and CLI. Panel localized (en, tr) from
  day one. Notes and decisions are user data, written in any language.

### Authentication

- **API:** one key, sent as a header. Generated on first `deming serve`, written
  to the user's config file; the CLI reads it from there. No copy-paste step.
- **Panel:** full ASP.NET Core Identity (`SignInManager`, the Identity cookie
  schemes) over Deming's own SQLite user store, no EF: one owner account,
  password, security stamp, lockout, two-step sign-in with an authenticator app
  and single-use recovery codes (stored hashed); no roles. Account pages: sign-in
  (keep me signed in, authenticator code, recovery code), first-visit
  registration, sign-out, password change, two-step sign-in on/off, new recovery
  codes, authenticator key reset, lockout. The key is shown as text and an
  otpauth URI (no QR code). Password hashing, TOTP and lockout come from
  Identity; only the store is written here. Identity cookies are always Secure
  (HTTPS only). The first visit creates the owner account.
  Login is required **even on loopback**: agents can drive browsers, and the
  panel is where a human cancels tasks and supersedes decisions. If an agent
  can reach it without credentials, the line between the agent's actions and
  the human's blurs. The API key is the agent's door; the panel login is the
  human's.

### Exposure guard

Deming holds a project's decisions, task list and notes. Someone moving it to
their own host must not be able to expose that by accident, so the protection
is enforced at startup, not described in the docs.

- **IP allow-list** (from Harness): every request — panel, API, static files —
  passes a gate. Entries are single addresses or CIDR ranges. The default list
  is loopback only (`127.0.0.1`, `::1`). An empty list blocks everything; it
  never means "open to all". `X-Forwarded-For` is ignored: the client writes
  that header.
- **Turnstile** on login and registration (from Harness). Off on loopback by
  default, so the local login page does not depend on reaching Cloudflare.
- **Startup refuses** when Deming listens on a non-loopback address unless all
  of these hold: the allow-list contains something other than loopback,
  Turnstile has real keys (Cloudflare's always-pass test keys are recognised
  and rejected), and the listener is HTTPS. The error names each missing item.
  In Harness this was an instruction ("replace the test keys if the IP
  restriction is lifted"); here it is code.
- **HTTPS only, loopback included**. With no address configured
  `deming serve` listens on `https://127.0.0.1:5001`; there is no http listener,
  and an explicit `http://` address is refused at startup ("http is not
  allowed, use https"). HSTS is on (browsers skip it for loopback); an HTTPS
  redirect has nothing to redirect from, and the panel's cookies are Secure.
- **Deming's own certificate** (not the ASP.NET development certificate,
  which needs the SDK). On first start `deming serve`
  generates a self-signed leaf certificate (not a CA; RSA 2048, SHA-256, one
  year) for `localhost`, `127.0.0.1` and `::1`, and keeps it in the data
  directory beside the database: `deming-https.pfx` (with the key, no
  password; owner-only on Unix) and `deming-https.crt` (public, PEM). Within 30
  days of its end, or expired, it is replaced at the next start. Its SHA-256
  fingerprint is written to the config file (`certificateSha256`). The CLI
  trusts what the system trusts plus that one certificate, by fingerprint;
  there is no accept-everything path, so a server elsewhere with its own
  certificate gets ordinary system validation. Browser trust is a separate,
  explicit command, `deming trust`: Windows `CurrentUser\Root`, macOS login
  keychain, on Linux it prints the manual steps (NSS `certutil`, Firefox,
  `update-ca-certificates`). `serve` never writes to a trust store. A
  certificate set in Kestrel's own configuration (`Kestrel:Certificates:Default`
  or an endpoint's `Certificate`) replaces Deming's own and none is generated.
  The ASP.NET development certificate is not looked for.
- Docker is not part of v1 (§ 2). Whoever adds it must handle one case: in a
  local container, requests arrive from the bridge network, not loopback. That
  needs an explicit, named setting, not a silent exception in the guard.

## 5. Rules carried over from Harness

These are the behaviours that make the method work. Each was added after a
measured failure; the reasons go into the code comments and decision records,
not just here.

### Tasks and locks

- State machine: `queued → in_progress → done | blocked | cancelled`.
  Status values are text with a CHECK constraint, not integers, so a raw
  `select` is readable.
- `next` takes the first eligible task, locks it to the session, and returns
  it with handoff notes and active decisions. The same session calling `next`
  again gets its held task back with the lock refreshed, not a new one.
- Session id is **required**. Without an owner, neither lock transfer nor
  timeout works.
- Lock timeout: 120 minutes without activity, configurable. Expired locks are
  released **lazily** on every `next` — no background service.
- Lock owner and the session that added the task are separate fields;
  otherwise a queued task looks locked.
- `take <id>` takes a specific task, skipping order and priority. It is
  refused only by obstacles that would lose information: lock held by another
  session, task done or cancelled, unfinished dependency. A blocked task can be
  taken — asking for it by id is the human intervention it was waiting for.
  Taking a task does not release other locks the session holds.
- `blocked` is temporary: lock released, task does not return to the queue.
  `cancelled` is permanent: reason required, task is never deleted, and it
  counts as done for tasks that depend on it.
- `requeue`: own lock, expired lock and blocked tasks are free; another
  session's live lock only with `--force`. Requeueing a queued, done or
  cancelled task is an error, not a silent success.
- `update` never changes status (sending a status is a 400). An in-progress
  task can only be edited by its lock holder; done/cancelled by nobody. One
  invalid dependency id means **no** field is written.
- `done --takeover` closes a task locked by another session in one step and
  records the previous owner in the note.
- Dependencies: a task does not enter the queue until all its dependencies are
  done or cancelled. Adding a dependency that creates a cycle is rejected.

### Handoff notes

- `done` without a summary is rejected.
- Field caps: summary 800, caveats 800, touched files 1500 characters. The API
  accepts **10 % more** than the instruction states: the model aims at the
  target and lands around it; rejecting a good 812-character note forces a
  retry, and the retry is where notes get lost. The tolerance does not replace
  the cap. Measured reason for the cap: unconstrained, the caveats field grew to
  ~2,200 characters on average, and the cost is paid by every session that reads it.
- `next` returns two kinds of notes **together**: notes of the tasks this one
  depends on, and the last three finished tasks of the project (excluding
  those already included). Never one *or* the other — they answer different
  questions.
- Touched files are a map, not a reading list. The agent instructions forbid
  opening them up front; reading four notes' worth of files cost 80–100k tokens
  before the first step.

### Decisions

- A decision belongs to the **project**, not to a task; the task id is a trace.
  Decisions survive task deletion.
- Never deleted. A changed decision becomes `superseded` and points to its
  replacement. The abandoned path is information.
- Decision text cap 400 (+10 %). Rationale has no cap and is **not** returned
  by `next`; it is fetched by id when someone wants to challenge the decision.
  Measured reason: with rationale, every `next` in a 100-decision project
  would carry ~11,000 extra tokens.
- In `next`, decision text is truncated at 250 characters with a
  `truncated: true` flag. Measured reason: a 36-decision project produced a
  47.7 KB `next` response, which the agent wrote to disk and read only the
  first 2 KB of — silently losing notes and decisions.

### Adding tasks

- Adding a task whose normalized title matches an unfinished task returns 409
  with the existing id; the CLI prints "already exists" and exits 4.
  Measured reason: one session posted five tasks twice within seconds. The
  instruction was there; it did not hold.
- If existing tasks cannot be read, the task is **not** added.
- The list endpoint returns order, id, status, priority, source, open
  dependency count and lock owner — never the scope.

### CLI behaviour

- Exit codes: 0 success, 1 error, 3 nothing to do (not an error), 4 already exists.
- Project name comes from a `.deming` file at the repository root, never from
  the directory name: a renamed clone would silently write to the wrong project.
- Session id: `DEMING_SESSION_ID`, otherwise `CLAUDE_CODE_SESSION_ID`, otherwise stop.
- No arguments or `help` prints usage; `help <command>` and every
  missing-argument error list the fields, as flags and as JSON names. Common
  synonyms are accepted.
- A command with a body (add, update, done, block, cancel, decide, finding)
  takes its fields as `--kebab-case` flags (`--depends-on 3,4`, `--touched-files`)
  or as one JSON object argument (or `-` for stdin). Reason: Windows PowerShell
  5.1 strips the double quotes of a JSON argument, and that is the default shell
  of every Windows machine (the first ten minutes). Both together
  is an error: a flag never overrides a JSON field, and nothing is sent. The
  exception is a switch (`--takeover`), which also goes with JSON. Numbers and
  id lists are converted; a bad value names the flag. The field list in the
  command table is the one source for the flags, the conversion and the help.
- Installed Claude Code commands are compared to the shipped ones **by content** on
  every call; a stale copy prints a warning and does not stop. Not installed
  (no `deming-*.md` in the command directory) means no warning.
- No positional arguments in slash command files; everything goes through
  `$ARGUMENTS`.

### Search

- `deming search <words>` looks in findings (title, body), decisions (title,
  text, rationale), notes (summary, caveats, touched files; block and cancel
  reasons too), tasks (title, scope) and messages (body).
- Words are split on spaces and **all** must occur. Matching uses the same
  folding as titles (case, dotted and dotless i, accents); no wildcards.
- `project` or `all=true` is required, never both. `all=true` includes inactive
  projects: their history is kept so that it can be found.
- Responses are narrow: type, id, project, title, where it matched and a short
  snippet, newest first; 20 by default, 100 at most. The full text is read by id.
- No full-text index: at a few thousand rows a scan takes milliseconds, and an
  index would add triggers to keep in step. Measure again before adding one.
- Search results are never part of `next`.

### Messages between agents

- A channel is a task; without a task it is the project's channel.
- A message is **data, not an instruction**: it hands over no lock, grants no
  right and is never part of `next`. `deming wait` ends its output saying so,
  because that output lands directly in an agent's context.
- The sender label comes from `DEMING_AGENT`; without it `deming message`
  stops. A guessed label would send the reply to another agent. Labels are
  lower-case ASCII, compared without case.
- Reading for a label returns what was written to it and to everyone, without
  its own messages to everyone.
- Nothing is marked read on the server. The cursor lives on the client, per
  session, project and channel (not per label: two sessions with one label
  would swallow each other's messages), and only `wait` moves it, after the
  message is printed.
- `wait` is a long poll: 0 a message came, 3 the time ran out, 1 an error. An
  unreachable server is never read as "no message".
- Ids are handed out in the order messages become visible, so a reader that
  remembers the last id never skips one. Tested with parallel writers.
- The channel of a finished or cancelled task is read-only. The channel does
  not replace the note: `done` reports the channel's message count and an
  estimate of unanswered questions, as information, not as a gate.
- Body cap 2000 characters; the database accepts 10 % more, like the other caps.

### Parallel work

- `deming parallel` lists what `next` could take now: queued, every dependency
  done or cancelled, project active. It is the same rule as the lock query; if
  one changes, the other must.
- `deming wt <id>` opens `<repo>-wt/task-<id>` on branch `task-<id>`, from the
  branch checked out in the main worktree. Files that are ignored by git but
  needed to work are listed in `.worktreeinclude`; if one is missing, nothing
  is opened. `.deming` is copied too.
- **"Done" means "on the main branch".** `deming merge` rebases the task branch
  onto the main branch inside the worktree (the session that knows the task
  resolves the conflict in its own tree), then moves the main branch by
  fast-forward only. A failed merge leaves nothing half-done. There is no
  "waiting for merge" status: the state machine stays small and "done" got a
  stricter meaning instead.
- **Workers do not push, and code enforces it.** `wt` writes a `pre-push` hook
  into the repository's hooks directory (following `core.hooksPath`); it
  refuses a push from a linked worktree and does nothing in the main one. A
  project's own `pre-push` is never overwritten: `wt` stops, and a
  `pre-push.local` is called by the Deming hook in the main worktree. A global
  `core.hooksPath` was not chosen: it would switch off every other
  repository's hooks.
- `deming wt-remove <id>` refuses an unmerged branch; there is no `--force`.
  Unmerged work is never deleted silently.
- Worktrees do not separate the server, the database or the ports. Parallelism
  moves file conflicts to merge time, it does not remove them: split tasks by
  file, and do shared groundwork (schema, models) in one task first.

## 6. Agent integration

The CLI is the integration surface; no MCP server in v1. Both installs are
explicit, run once by the user: `deming serve` only names them
on its first start and never writes into an agent's directory.

- **Claude Code (v1):** `deming install claude` writes the command files,
  embedded in the binary, straight into Claude Code's user command directory:
  `CLAUDE_CONFIG_DIR/commands` when that is set, otherwise `~/.claude/commands`
  (`--dir` or `DEMING_CLAUDE_DIR` choose another). The files are named
  `deming-*.md`, so the commands are `/deming-next`, `/deming-done`, ... and
  the prefix keeps them apart from the user's own `/next` or `/done`. Every
  file Deming writes carries the marker `<!-- deming:command -->`; only marked
  files are replaced or removed. An unmarked `deming-*.md` is the user's own:
  it is never deleted, and a name clash stops the install before anything is
  written. Every path written is printed.
- **Other agents (v1):** `deming install agents` writes an agent-neutral block
  into `AGENTS.md` at the repository root, between `<!-- deming:begin -->` and
  `<!-- deming:end -->`: which `deming` command to run when, the session id
  (`DEMING_SESSION_ID`), stop on error, how to write a handoff note, do not
  open touched files up front. It creates the file if needed, replaces the
  block in place, never touches text outside it and leaves the file untouched
  when the block is current. `--print` only prints the block.

## 7. Left behind

Not carried over from Harness: the custom data and identity libraries (their
conventions are re-implemented for SQLite inside Deming), SQL
Server and stored procedures, IIS deployment and the GitLab pipeline, and
anything tied to the author's server. The IP allow-list and Turnstile are
**kept** (see § 4, Exposure guard).

## 8. Migration from Harness (author only)

The author moves to Deming once v1 matches the Harness core. A one-off importer
copies projects, tasks, dependencies, notes and decisions (including
superseded chains). It is not part of this repository or of any release; its
input is a JSON export file, never a live Harness connection.

## 9. Open questions

1. README epigraph: verify the source of the Deming quote before using it.

Decided:

- Directories: the config file is `<ApplicationData>/Deming/config.json` (Windows `%APPDATA%`,
  Linux and macOS `~/.config`), the data directory is `<LocalApplicationData>/Deming`. `DEMING_CONFIG`
  overrides the config file path. The first `deming serve` generates the API key and writes it to the
  config file (owner-only on Unix, atomic, unknown fields kept); the CLI reads it from there, or from
  `DEMING_API_KEY`. Server address: `DEMING_URL` or `url` in the file, default `https://127.0.0.1:5001`; an `http://` address is refused.
  `deming serve` also writes `certificateSha256`, the fingerprint of its HTTPS certificate (§ 4), which
  the CLI trusts; the certificate files live in the data directory beside the database.

- Panel login is required on loopback too (see § 4, Authentication).
- License: **Apache-2.0**. Derivatives must keep the `NOTICE` file, which
  carries attribution further than MIT does.
- Commit author email: the author's personal address (already public elsewhere).
