-- Finding.Insert: records a finding. Title/body rules (not blank, caps) are CHECK constraints and
-- the same-project traces are triggers; none is repeated in C#.
-- Params: @ProjectId, @TaskId (nullable), @DecisionId (nullable), @Title, @Body, @SessionId (nullable)
INSERT INTO finding (project_id, task_id, decision_id, title, body, session_id)
VALUES (@ProjectId, @TaskId, @DecisionId, trim(@Title), trim(@Body), @SessionId)
RETURNING *;
