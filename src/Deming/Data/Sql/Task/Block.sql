-- Task.Block: temporary stop (scope § 5). Lock released, task does NOT return to the queue.
-- Params: @Id, @Reason
UPDATE task
SET status = 'blocked',
    lock_owner = NULL,
    locked_at = NULL,
    blocked_reason = @Reason
WHERE id = @Id
RETURNING *;
