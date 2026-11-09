-- Task.Complete: in progress -> done. Lock released, blocked_reason cleared.
-- Params: @Id, @Now
UPDATE task
SET status = 'done',
    lock_owner = NULL,
    locked_at = NULL,
    blocked_reason = NULL,
    finished_at = @Now
WHERE id = @Id
RETURNING *;
