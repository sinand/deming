---
description: Search the project's findings, decisions, handoff notes, tasks and channel messages
argument-hint: <words> [--all] [--type finding|decision|note|task|message] [--top <n>]
allowed-tools: Bash(deming *), PowerShell(deming *)
---
<!-- deming:command -->

Search Deming.

Arguments: $ARGUMENTS

1. Run `deming search <words>` with the words from the arguments. Every word must occur; case and accents do not matter. Add `--all` to search every project, `--type finding|decision|note|task|message` to search one kind, `--top <n>` (at most 100) for more hits, when the user asked for those.
2. Exit code 3 means nothing matched: say so. Do not retry with other words unless the user asks. Run the command alone, with nothing chained after it (no `; echo $?`): the tool reports the exit code itself, and a chained command asks the user for permission.
3. The output holds snippets, not the full text. Read a hit only when you need it: `deming findings <id>`, `deming decisions <id>`, `deming show <id>` (for a note, show its `taskId`), `deming messages <id>` (a message).
4. If the output is an error, STOP and report it.
