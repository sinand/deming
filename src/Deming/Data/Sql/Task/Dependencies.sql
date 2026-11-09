-- Task.Dependencies: the tasks a task waits for, with their current status.
-- Params: @Id
SELECT dep.id     AS id,
       dep.title  AS title,
       dep.status AS status
FROM task_dependency d
JOIN task dep ON dep.id = d.depends_on_task_id
WHERE d.task_id = @Id
ORDER BY dep.id;
