-- Decision.Insert: records an active decision. Title/text rules (not blank, text <= 440) are
-- CHECK constraints, not repeated in C#.
-- Params: @ProjectId, @TaskId (nullable), @Title, @Text, @Rationale (nullable), @SessionId (nullable)
INSERT INTO decision (project_id, task_id, title, text, rationale, session_id)
VALUES (@ProjectId, @TaskId, trim(@Title), trim(@Text), nullif(trim(@Rationale), ''), @SessionId)
RETURNING *;
