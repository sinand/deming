---
description: Record a decision, or replace an earlier one
argument-hint: <JSON object with title, text, rationale> [--supersede <decision id>]
allowed-tools: Bash(deming:*)
---
<!-- deming:command -->

Record a decision in Deming.

Arguments: $ARGUMENTS

1. Build the JSON object: `title` (required), `text` (required, at most 400 characters, the decision itself), `rationale` (why; no limit, not shown by next) and `taskId` (the task it was taken in) when known. If the arguments already hold a JSON object, use it.
2. Run `deming decide '<json>'`, or `deming decide -` with the JSON on stdin when it holds quotes. In a shell that mangles the quotes of a JSON argument (Windows PowerShell 5.1), use flags instead: `deming decide --title "..." --text "..." --rationale "..."`.
3. To replace an earlier decision, add `--supersede <decision id>`; the old one becomes superseded.
4. If the output is an error, STOP and report it.
