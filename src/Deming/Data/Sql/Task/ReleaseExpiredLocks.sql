-- Task.ReleaseExpiredLocks: lazy lock cleanup (scope § 5). In-progress tasks whose lock was
-- last refreshed before @Cutoff go back to the queue. No background service calls this; every
-- `next` / `take` does, inside its transaction. started_at is kept (the work did start).
-- Params: @Cutoff (now - lock timeout). Returns the number of released tasks.
UPDATE task
SET status = 'queued',
    lock_owner = NULL,
    locked_at = NULL
WHERE status = 'in_progress'
  AND locked_at < @Cutoff;
