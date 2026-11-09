-- Decision.ListAll: decisions across projects for the panel's Decisions page (same row shape as
-- Decision.List, plus the project). Full text, NO rationale. Newest first.
-- Params: @ProjectId (nullable = every project), @ActiveOnly (1 = only active projects when
-- @ProjectId is null), @Status (nullable = all).
SELECT d.id,
       d.title,
       d.text,
       d.status,
       d.superseded_by_id,
       d.task_id,
       d.created_at,
       p.id        AS project_id,
       p.name      AS project_name,
       p.is_active AS project_is_active
FROM decision d
JOIN project p ON p.id = d.project_id
WHERE (@ProjectId IS NULL OR d.project_id = @ProjectId)
  AND (@ProjectId IS NOT NULL OR @ActiveOnly = 0 OR p.is_active = 1)
  AND (@Status IS NULL OR d.status = @Status)
ORDER BY d.id DESC;
