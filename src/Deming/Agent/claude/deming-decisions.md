---
description: List the project's decisions, or read one with its rationale
argument-hint: [decision id]
allowed-tools: Bash(deming:*)
---
<!-- deming:command -->

Read Deming decisions.

Arguments: $ARGUMENTS

1. If the arguments are empty, run `deming decisions` (add `--status active` or `--status superseded` if the user asked for one kind).
2. If they hold a decision id, run `deming decisions <id>`; that output includes the rationale.
3. Active decisions are binding. Superseded ones are history.
4. If the output is an error, STOP and report it.
