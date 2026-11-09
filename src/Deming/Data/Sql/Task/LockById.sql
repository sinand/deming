-- Task.LockById: `take <id>`. Order and priority are ignored. Allowed from queued and blocked
-- (asking for a blocked task by id is the human intervention it waited for); refused while any
-- dependency is neither done nor cancelled. blocked_reason is kept as history.
-- Must run inside a BEGIN IMMEDIATE transaction.
-- Params: @Id, @SessionId, @Now. No row when a rule refuses; the service explains why.
UPDATE task
SET status = 'in_progress',
    lock_owner = @SessionId,
    locked_at = @Now,
    started_at = coalesce(started_at, @Now)
WHERE id = @Id
  AND status IN ('queued', 'blocked')
  AND NOT EXISTS (
      SELECT 1
      FROM task_dependency d
      JOIN task dep ON dep.id = d.depends_on_task_id
      WHERE d.task_id = task.id
        AND dep.status NOT IN ('done', 'cancelled'))
RETURNING *;
