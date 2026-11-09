-- Task.Cancel: permanent (scope § 5). The row stays; dependents treat it as done.
-- Params: @Id, @Reason, @Now
UPDATE task
SET status = 'cancelled',
    lock_owner = NULL,
    locked_at = NULL,
    blocked_reason = NULL,
    cancelled_reason = @Reason,
    finished_at = @Now
WHERE id = @Id
RETURNING *;
