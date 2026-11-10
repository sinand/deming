---
description: Record a finding (a measurement or observation that is not a decision)
argument-hint: <JSON object with title, body, taskId, decisionId>
allowed-tools: Bash(deming *), PowerShell(deming *)
---
<!-- deming:command -->

Record a finding in Deming. A finding is a measurement or observation worth keeping that is not a decision (a distribution table, a list of splits, a narrative of what broke). It is not shown by next; it is read by id.

Arguments: $ARGUMENTS

1. Build the JSON object: `title` (required, at most 200 characters) and `body` (required, markdown, at most 10000 characters: the numbers, the table, the story). Add `taskId` (the task it was found in) and `decisionId` (a decision it relates to) when known. If the arguments already hold a JSON object, use it.
2. Run `deming finding '<json>'`, or `deming finding -` with the JSON on stdin when it holds quotes. In a shell that mangles the quotes of a JSON argument (Windows PowerShell 5.1), use flags instead: `deming finding --title "..." --body "..."`. For a long body, write it to a file and run `deming finding '{"title":"..."}' --body-file <path>`.
3. If the output is an error, STOP and report it.
