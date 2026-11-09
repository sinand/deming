-- Finding.ListAll: findings across projects for the panel's Findings page (same row shape as
-- Finding.List, plus the project). NO body. Newest first.
-- Params: @ProjectId (nullable = every project), @ActiveOnly (1 = only active projects when
-- @ProjectId is null).
SELECT f.id,
       f.title,
       length(f.body) AS body_length,
       f.task_id,
       f.decision_id,
       f.session_id,
       f.created_at,
       p.id        AS project_id,
       p.name      AS project_name,
       p.is_active AS project_is_active
FROM finding f
JOIN project p ON p.id = f.project_id
WHERE (@ProjectId IS NULL OR f.project_id = @ProjectId)
  AND (@ProjectId IS NOT NULL OR @ActiveOnly = 0 OR p.is_active = 1)
ORDER BY f.id DESC;
