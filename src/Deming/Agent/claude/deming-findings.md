---
description: List the project's findings, or read one with its body
argument-hint: [finding id] [--task <id>] [--decision <id>]
allowed-tools: Bash(deming *), PowerShell(deming *)
---
<!-- deming:command -->

Read Deming findings.

Arguments: $ARGUMENTS

1. If the arguments are empty, run `deming findings` (newest first, titles and body lengths only). Add `--task <id>` or `--decision <id>` to narrow the list when the user asked for those.
2. If they hold a finding id, run `deming findings <id>`; that output includes the body.
3. Findings are observations, not rules. Decisions are the binding ones.
4. If the output is an error, STOP and report it.
