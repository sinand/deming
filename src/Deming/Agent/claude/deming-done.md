---
description: Close the task you are working on with a handoff note
argument-hint: <task id> <summary, or a JSON object>
allowed-tools: Bash(deming *), PowerShell(deming *)
---
<!-- deming:command -->

Close a Deming task with a handoff note.

Arguments: $ARGUMENTS

1. The first word of the arguments is the task id; the rest is the summary. If the id is missing, use the id of the task you took in this session.
2. Write the note for the next session, not for a reviewer: `summary` (what changed and where it lives, at most 800 characters), `caveats` (what the next person must watch for, at most 800), `touchedFiles` (one plain string, not an object or array: the paths and what changed in each, at most 1500 characters; a guide to where to look, not a reading list) and `commitHash` when you committed.
3. Run `deming done <id> '<json>'`, or pipe the JSON through stdin with `deming done <id> -` when it holds quotes. Example JSON: `{"summary": "...", "caveats": "...", "touchedFiles": "...", "commitHash": "..."}`. In a shell that mangles the quotes of a JSON argument (Windows PowerShell 5.1), use flags instead: `deming done <id> --summary "..." --caveats "..." --touched-files "..." --commit-hash <hash>`.
4. If the output is an error, STOP and report it. A task held by another session needs `--takeover`; use it only if the user said so.
