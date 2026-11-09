-- Task.List: the list endpoint's rows (scope § 5, "Adding tasks"): order, id, status, priority,
-- source, open dependency count, lock owner. NEVER the scope: it is the shape of the response.
-- Order: the queue order (priority, sort_order, id). Params: @ProjectId, @Status (nullable = all),
-- @EligibleOnly (1 = only what next could take now: the same rule as Task.LockNext, queued with every
-- dependency done or cancelled, in an active project; change both together).
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
       t.locked_at  AS locked_at
FROM task t
JOIN project p ON p.id = t.project_id
WHERE t.project_id = @ProjectId
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
