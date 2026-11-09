-- Project.List: all projects by name, with their task counts, in one query (no per-project query).
-- total    : tasks that can still be finished or are finished (cancelled ones are left out)
-- done     : finished tasks
-- queued   : tasks waiting in the queue
SELECT p.*,
       coalesce(c.total, 0)  AS total_tasks,
       coalesce(c.done, 0)   AS done_tasks,
       coalesce(c.queued, 0) AS queued_tasks
FROM project p
LEFT JOIN (
    SELECT project_id,
           sum(status <> 'cancelled') AS total,
           sum(status = 'done')       AS done,
           sum(status = 'queued')     AS queued
    FROM task
    GROUP BY project_id
) c ON c.project_id = p.id
ORDER BY p.name_normalized;
