-- HandoffNote.Insert: a note on a task. kind 'handoff' is written by done; 'note' by block/cancel.
-- Caps are CHECKed by the schema (880 / 880 / 1650).
-- Params: @TaskId, @Kind, @Summary, @Caveats, @TouchedFiles, @CommitHash, @SessionId, @TakenOverFrom (all but the first three nullable)
INSERT INTO handoff_note (task_id, kind, summary, caveats, touched_files, commit_hash, session_id, taken_over_from)
VALUES (@TaskId, @Kind, @Summary, @Caveats, @TouchedFiles, @CommitHash, @SessionId, @TakenOverFrom)
RETURNING *;
