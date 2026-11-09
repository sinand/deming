---
description: Take the next task from the Deming queue (or a specific task id), with handoff notes and decisions
argument-hint: [task id]
allowed-tools: Bash(deming:*)
---
<!-- deming:command -->

Take a task from the Deming queue.

Arguments: $ARGUMENTS

1. If the arguments above are empty, run `deming next`. If they hold a task id, run `deming take <id>` with that id.
2. Exit code 3 means nothing is eligible: say so and stop. Do not invent work.
3. On success the output is JSON: the task (title, scope), `notes` (handoff notes from dependencies and recent tasks) and `decisions` (active, binding). Read the notes' summary and caveats before touching any file; do not open the files they list ahead of need. A decision with `truncated: true` has more text: run `deming decisions <id>` for it.
4. If you hit an error, STOP and report it. Do not work around it.
5. Do the task as its scope says. When you finish, use /deming-done; when you cannot continue, /deming-block.
