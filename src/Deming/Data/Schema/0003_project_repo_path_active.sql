-- ---------------------------------------------------------------------------------------
-- project: repo path and active flag
-- ---------------------------------------------------------------------------------------
-- A project is never deleted; it is made inactive. History (tasks, notes, decisions, findings)
-- stays. repo_path is informational (where the .deming file was written); the project's
-- identity is still the name in the .deming file.

ALTER TABLE project ADD COLUMN repo_path TEXT
    CONSTRAINT ck_project_repo_path_not_blank CHECK (repo_path IS NULL OR trim(repo_path) <> '');

ALTER TABLE project ADD COLUMN is_active INTEGER NOT NULL DEFAULT 1
    CONSTRAINT ck_project_is_active_flag CHECK (is_active IN (0, 1));

-- An inactive project takes no new tasks (add) ...
CREATE TRIGGER tr_task_project_active_insert
BEFORE INSERT ON task
FOR EACH ROW
WHEN (SELECT is_active FROM project WHERE id = NEW.project_id) = 0
BEGIN
    SELECT RAISE(ABORT, 'project_inactive');
END;

-- ... and hands out no locks (next, take, and the lock refresh of a session that still holds one).
-- Releasing a lock (lock_owner becomes NULL) stays possible: requeue, block, done, cancel.
CREATE TRIGGER tr_task_project_active_lock
BEFORE UPDATE OF status, lock_owner, locked_at ON task
FOR EACH ROW
WHEN NEW.lock_owner IS NOT NULL
 AND (NEW.status IS NOT OLD.status OR NEW.lock_owner IS NOT OLD.lock_owner OR NEW.locked_at IS NOT OLD.locked_at)
 AND (SELECT is_active FROM project WHERE id = NEW.project_id) = 0
BEGIN
    SELECT RAISE(ABORT, 'project_inactive');
END;
