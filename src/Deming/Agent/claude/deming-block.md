---
description: Park a task you cannot continue, with the reason
argument-hint: <task id> <reason>
allowed-tools: Bash(deming:*)
---
<!-- deming:command -->

Block a Deming task you cannot continue.

Arguments: $ARGUMENTS

1. The first word of the arguments is the task id; the rest is the reason. If the id is missing, use the id of the task you took in this session.
2. The reason must say what stops the work and what is needed to resume it, in plain words.
3. Run `deming block <id> <reason>`. This releases the lock and records the reason as a note.
4. If the output is an error, STOP and report it.
