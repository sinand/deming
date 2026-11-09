-- Task.ListAll: tasks across projects for the panel's Tasks page (same row shape as Task.List,
-- plus the project). NEVER the scope. One query, no per-project lookups.
-- Params: @ProjectId (nullable = every project), @ActiveOnly (1 = only active projects when
-- @ProjectId is null), @Status (nullable = all), @EligibleOnly (1 = only what next could take now:
-- the same rule as Task.LockNext and Task.List; change them together).
SELECT t.sort_order AS sort_order,
       t.id         AS id,
       t.title      AS title,
       t.status     AS status,
       t.priority   AS priority,
       t.source     AS source,
       (SELECT count(*)
        FROM task_dependency d
        JOIN task dep ON dep.id = d.depends_on_task_id
        WHERE d.task_id = t.id
          AND dep.status NOT IN ('done', 'cancelled')) AS open_dependency_count,
       t.lock_owner AS lock_owner,
       t.locked_at  AS locked_at,
       p.id         AS project_id,
       p.name       AS project_name,
       p.is_active  AS project_is_active
FROM task t
JOIN project p ON p.id = t.project_id
WHERE (@ProjectId IS NULL OR t.project_id = @ProjectId)
  AND (@ProjectId IS NOT NULL OR @ActiveOnly = 0 OR p.is_active = 1)
  AND (@Status IS NULL OR t.status = @Status)
  AND (@EligibleOnly = 0
       OR (p.is_active = 1
           AND t.status = 'queued'
           AND NOT EXISTS (
               SELECT 1
               FROM task_dependency d
               JOIN task dep ON dep.id = d.depends_on_task_id
               WHERE d.task_id = t.id
                 AND dep.status NOT IN ('done', 'cancelled'))))
ORDER BY t.priority, t.sort_order, t.id;
