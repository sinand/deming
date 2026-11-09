-- Finding.List: findings of a project for the list endpoint, newest first. NO body (it is fetched
-- by id: Finding.GetById); body_length tells how much there is to read. Explicit projection =
-- the response shape.
-- Params: @ProjectId, @TaskId (nullable = any), @DecisionId (nullable = any).
SELECT id,
       title,
       length(body) AS body_length,
       task_id,
       decision_id,
       session_id,
       created_at
FROM finding
WHERE project_id = @ProjectId
  AND (@TaskId IS NULL OR task_id = @TaskId)
  AND (@DecisionId IS NULL OR decision_id = @DecisionId)
ORDER BY id DESC;
