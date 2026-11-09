-- Task.LockNext: locks the first eligible task of a project to the session (one statement).
-- Eligible: queued, and every dependency is done or cancelled. Blocked tasks are never picked.
-- Task.List and Task.ListAll (@EligibleOnly) state the same rule for 'deming parallel'; change them together.
-- Order: priority (1 first), sort_order, id. Must run inside a BEGIN IMMEDIATE transaction.
-- Params: @ProjectId, @SessionId, @Now. No row when nothing is eligible.
UPDATE task
SET status = 'in_progress',
    lock_owner = @SessionId,
    locked_at = @Now,
    started_at = coalesce(started_at, @Now)
WHERE id = (
    SELECT t.id
    FROM task t
    WHERE t.project_id = @ProjectId
      AND t.status = 'queued'
      AND NOT EXISTS (
          SELECT 1
          FROM task_dependency d
          JOIN task dep ON dep.id = d.depends_on_task_id
          WHERE d.task_id = t.id
            AND dep.status NOT IN ('done', 'cancelled'))
    ORDER BY t.priority, t.sort_order, t.id
    LIMIT 1)
RETURNING *;
