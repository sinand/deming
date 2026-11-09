-- Task.RefreshLock: the lock holder shows activity; the timeout clock restarts.
-- Params: @Id, @SessionId, @Now. No row when the session does not hold the lock.
UPDATE task
SET locked_at = @Now
WHERE id = @Id
  AND status = 'in_progress'
  AND lock_owner = @SessionId
RETURNING *;
