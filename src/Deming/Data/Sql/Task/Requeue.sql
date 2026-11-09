-- Task.Requeue: back to the queue. Lock released; blocked_reason cleared (take keeps it as
-- history, requeue ends the blocked episode). started_at is kept: the work did start.
-- The service decides who may do this. Must run inside a transaction.
-- Params: @Id
UPDATE task
SET status = 'queued',
    lock_owner = NULL,
    locked_at = NULL,
    blocked_reason = NULL
WHERE id = @Id
RETURNING *;
