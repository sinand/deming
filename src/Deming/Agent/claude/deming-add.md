---
description: Add a task to the Deming queue
argument-hint: <title, or a JSON object with title, scope, priority, dependsOn>
allowed-tools: Bash(deming:*)
---
<!-- deming:command -->

Add a task to the Deming queue.

Arguments: $ARGUMENTS

1. If the arguments start with `{`, they are the JSON object: pass them through. Otherwise build one: `{"title": "<the arguments>"}`. Add `scope` (what to do, in the task's own words), `priority` (1 high to 3 low, default 2) and `dependsOn` (task ids) only when the user gave them.
2. Run `deming add '<json>'`, or `deming add -` with the JSON on stdin when it holds quotes. In a shell that mangles the quotes of a JSON argument (Windows PowerShell 5.1), use flags instead: `deming add --title "..." --scope "..." --priority 1 --depends-on 3,4`.
3. Exit code 4 means an unfinished task with the same title exists: report its id and do not add it again.
4. If the output is another error, STOP and report it. Do not retry in a loop.
