-- Deming schema 0004: findings.
-- A finding is a measurement or observation that is not a decision (a distribution table, a
-- narrative of broken rings, a list of splits). It is not a task (work to do), not a decision
-- (a departure from something) and not a handoff note (what happened in one task, which slides
-- out of the window three tasks later).
--
-- Belongs to the project and lives forever: never deleted, never rewritten. It is NOT part of
-- the `next` response (context budget); it is read by id or listed without its body.
-- task_id / decision_id are traces only. A task may disappear (SET NULL); decisions never do.
-- Caps: title 200 (as in Harness); body target 10000, the database accepts 10 % more (11000),
-- like the other caps. Measured on 27 Harness findings: longest 7543, mean 1909. Body is markdown.

CREATE TABLE finding (
    id          INTEGER PRIMARY KEY,
    project_id  INTEGER NOT NULL REFERENCES project (id),
    task_id     INTEGER REFERENCES task (id) ON DELETE SET NULL,
    decision_id INTEGER REFERENCES decision (id),
    title       TEXT    NOT NULL,
    body        TEXT    NOT NULL,
    session_id  TEXT,
    created_at  TEXT    NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),

    CONSTRAINT ck_finding_title CHECK (trim(title) <> '' AND length(title) <= 200),
    CONSTRAINT ck_finding_body  CHECK (trim(body) <> '' AND length(body) <= 11000)
);

-- Listing: newest first within a project, optionally narrowed by task or decision.
CREATE INDEX ix_finding_project  ON finding (project_id, id);
CREATE INDEX ix_finding_task     ON finding (task_id)     WHERE task_id IS NOT NULL;
CREATE INDEX ix_finding_decision ON finding (decision_id) WHERE decision_id IS NOT NULL;

-- The traces must belong to the finding's own project.
CREATE TRIGGER tr_finding_task_same_project
BEFORE INSERT ON finding
FOR EACH ROW
WHEN NEW.task_id IS NOT NULL
 AND (SELECT project_id FROM task WHERE id = NEW.task_id) IS NOT NEW.project_id
BEGIN
    SELECT RAISE(ABORT, 'finding_task_other_project');
END;

CREATE TRIGGER tr_finding_decision_same_project
BEFORE INSERT ON finding
FOR EACH ROW
WHEN NEW.decision_id IS NOT NULL
 AND (SELECT project_id FROM decision WHERE id = NEW.decision_id) IS NOT NEW.project_id
BEGIN
    SELECT RAISE(ABORT, 'finding_decision_other_project');
END;

CREATE TRIGGER tr_finding_no_delete
BEFORE DELETE ON finding
BEGIN
    SELECT RAISE(ABORT, 'finding_never_deleted');
END;

-- The record is the text; a correction is a new finding. (task_id may still be cleared by the
-- foreign key when its task is deleted, so it is not in the column list.)
CREATE TRIGGER tr_finding_immutable
BEFORE UPDATE OF project_id, decision_id, title, body, session_id, created_at ON finding
FOR EACH ROW
BEGIN
    SELECT RAISE(ABORT, 'finding_immutable');
END;
