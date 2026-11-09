-- Task.GetHeld: the task of this project the session holds a lock on. A session can hold
-- several (`take` releases nothing); the most recently locked one wins.
-- Params: @ProjectId, @SessionId
SELECT *
FROM task
WHERE project_id = @ProjectId
  AND status = 'in_progress'
  AND lock_owner = @SessionId
ORDER BY locked_at DESC, id DESC
LIMIT 1;
