-- Task.PendingDependencies: prerequisites of a task that are neither done nor cancelled.
-- Params: @Id
SELECT dep.*
FROM task_dependency d
JOIN task dep ON dep.id = d.depends_on_task_id
WHERE d.task_id = @Id
  AND dep.status NOT IN ('done', 'cancelled')
ORDER BY dep.sort_order, dep.id;
