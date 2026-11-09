-- HandoffNote.ByTask: every note of one task, oldest first (the task detail endpoint).
-- Params: @TaskId
SELECT *
FROM handoff_note
WHERE task_id = @TaskId
ORDER BY id;
