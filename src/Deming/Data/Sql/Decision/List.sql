-- Decision.List: decisions of a project for the list endpoint. Full text, NO rationale
-- (the rationale is fetched by id: Decision.GetById). Explicit projection = the response shape.
-- Params: @ProjectId, @Status (nullable = all).
SELECT id,
       title,
       text,
       status,
       superseded_by_id,
       task_id,
       created_at
FROM decision
WHERE project_id = @ProjectId
  AND (@Status IS NULL OR status = @Status)
ORDER BY id;
