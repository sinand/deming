---
description: Cancel a task that will not be done, with the reason
argument-hint: <task id> <reason>
allowed-tools: Bash(deming *), PowerShell(deming *)
---
<!-- deming:command -->

Cancel a Deming task for good (it is kept, never deleted).

Arguments: $ARGUMENTS

1. The first word of the arguments is the task id; the rest is the reason. Cancel only when the user decided the task will not be done; if you merely cannot continue, use /deming-block instead.
2. Run `deming cancel <id> <reason>`.
3. If the output is an error, STOP and report it.
