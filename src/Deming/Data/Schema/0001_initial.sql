-- Deming schema 0001: the core.
-- Projects, tasks, dependencies, locks, handoff notes, decisions, the panel owner account
-- and the Data Protection key ring. Findings and messages come in later migrations.
--
-- Conventions
--   * Tables and columns: English, singular, snake_case.
--   * Status / source / kind: TEXT with a named CHECK, so a raw SELECT is readable.
--   * Timestamps: TEXT, UTC, ISO 8601 with milliseconds: 2026-11-09T01:45:01.123Z.
--     Same format as the C# parameter binder (SqlValue), so text comparison is time order.
--   * Constraints and triggers are named (ck_*, ux_*, ix_*, tr_*). Trigger errors are
--     snake_case codes; services map them to API errors (ConstraintViolationException).
--   * Normalized titles are produced by deming_normalize(), a function registered on every
--     connection by DemingDb (TextNormalizer). Insert/update SQL calls it; C# does not.

-- ---------------------------------------------------------------------------------------
-- project
-- ---------------------------------------------------------------------------------------
CREATE TABLE project (
    id              INTEGER PRIMARY KEY,
    name            TEXT    NOT NULL,
    name_normalized TEXT    NOT NULL,
    description     TEXT,
    created_at      TEXT    NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
    updated_at      TEXT    NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
    CONSTRAINT ck_project_name_not_blank CHECK (trim(name) <> '' AND name_normalized <> '')
);

-- The .deming file names the project; two names that differ only in case/diacritics are one project.
CREATE UNIQUE INDEX ux_project_name ON project (name_normalized);

-- ---------------------------------------------------------------------------------------
-- task
-- ---------------------------------------------------------------------------------------
-- State machine: queued -> in_progress -> done | blocked | cancelled (scope § 5).
-- lock_owner is the session holding the lock; added_by_session is who created the task.
-- They are separate on purpose: otherwise a queued task looks locked.
CREATE TABLE task (
    id                INTEGER PRIMARY KEY,
    project_id        INTEGER NOT NULL REFERENCES project (id) ON DELETE CASCADE,
    sort_order        INTEGER NOT NULL,
    title             TEXT    NOT NULL,
    title_normalized  TEXT    NOT NULL,
    scope             TEXT,
    status            TEXT    NOT NULL DEFAULT 'queued',
    priority          INTEGER NOT NULL DEFAULT 2,
    source            TEXT    NOT NULL DEFAULT 'agent',
    added_by_session  TEXT,
    lock_owner        TEXT,
    locked_at         TEXT,
    started_at        TEXT,
    finished_at       TEXT,
    blocked_reason    TEXT,
    cancelled_reason  TEXT,
    created_at        TEXT    NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
    updated_at        TEXT    NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),

    CONSTRAINT ck_task_status
        CHECK (status IN ('queued', 'in_progress', 'done', 'blocked', 'cancelled')),
    CONSTRAINT ck_task_source
        CHECK (source IN ('human', 'agent')),
    CONSTRAINT ck_task_priority
        CHECK (priority BETWEEN 1 AND 3),
    CONSTRAINT ck_task_title_not_blank
        CHECK (trim(title) <> '' AND title_normalized <> ''),
    -- A lock owner exists exactly while the task is in progress, and a lock has a time.
    CONSTRAINT ck_task_lock_only_in_progress
        CHECK ((status = 'in_progress') = (lock_owner IS NOT NULL AND lock_owner <> '')),
    CONSTRAINT ck_task_lock_has_time
        CHECK ((lock_owner IS NULL) = (locked_at IS NULL)),
    CONSTRAINT ck_task_blocked_has_reason
        CHECK (status <> 'blocked' OR trim(coalesce(blocked_reason, '')) <> ''),
    CONSTRAINT ck_task_cancelled_has_reason
        CHECK (status <> 'cancelled' OR trim(coalesce(cancelled_reason, '')) <> ''),
    CONSTRAINT ck_task_finished_has_time
        CHECK (status NOT IN ('done', 'cancelled') OR finished_at IS NOT NULL)
);

-- Duplicate guard: one unfinished task per normalized title and project (409 on violation).
CREATE UNIQUE INDEX ux_task_open_title
    ON task (project_id, title_normalized)
    WHERE status IN ('queued', 'in_progress', 'blocked');

-- `next` scans eligible tasks of a project in order.
CREATE INDEX ix_task_queue ON task (project_id, status, priority, sort_order, id);

-- "Which task does this session hold?"
CREATE INDEX ix_task_lock_owner ON task (lock_owner) WHERE lock_owner IS NOT NULL;

CREATE TRIGGER tr_task_updated_at
AFTER UPDATE ON task
FOR EACH ROW WHEN NEW.updated_at = OLD.updated_at
BEGIN
    UPDATE task SET updated_at = strftime('%Y-%m-%dT%H:%M:%fZ', 'now') WHERE id = NEW.id;
END;

CREATE TRIGGER tr_project_updated_at
AFTER UPDATE ON project
FOR EACH ROW WHEN NEW.updated_at = OLD.updated_at
BEGIN
    UPDATE project SET updated_at = strftime('%Y-%m-%dT%H:%M:%fZ', 'now') WHERE id = NEW.id;
END;

-- A task never moves to another project: its dependencies, notes and decisions would dangle.
CREATE TRIGGER tr_task_project_immutable
BEFORE UPDATE OF project_id ON task
FOR EACH ROW WHEN NEW.project_id <> OLD.project_id
BEGIN
    SELECT RAISE(ABORT, 'task_project_immutable');
END;

-- ---------------------------------------------------------------------------------------
-- task_dependency
-- ---------------------------------------------------------------------------------------
-- task_id waits for depends_on_task_id (done or cancelled releases it).
CREATE TABLE task_dependency (
    task_id            INTEGER NOT NULL REFERENCES task (id) ON DELETE CASCADE,
    depends_on_task_id INTEGER NOT NULL REFERENCES task (id) ON DELETE CASCADE,
    created_at         TEXT    NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
    PRIMARY KEY (task_id, depends_on_task_id),
    CONSTRAINT ck_task_dependency_not_self CHECK (task_id <> depends_on_task_id)
) WITHOUT ROWID;

-- Reverse lookup: "who waits for this task?"
CREATE INDEX ix_task_dependency_depends_on ON task_dependency (depends_on_task_id, task_id);

CREATE TRIGGER tr_task_dependency_same_project
BEFORE INSERT ON task_dependency
FOR EACH ROW
WHEN (SELECT project_id FROM task WHERE id = NEW.task_id)
  <> (SELECT project_id FROM task WHERE id = NEW.depends_on_task_id)
BEGIN
    SELECT RAISE(ABORT, 'dependency_other_project');
END;

-- Cycle detection: walking the dependencies of the new prerequisite must not reach the task.
CREATE TRIGGER tr_task_dependency_no_cycle
BEFORE INSERT ON task_dependency
FOR EACH ROW
WHEN EXISTS (
    WITH RECURSIVE reach (id) AS (
        SELECT NEW.depends_on_task_id
        UNION
        SELECT d.depends_on_task_id
        FROM task_dependency d
        JOIN reach r ON d.task_id = r.id
    )
    SELECT 1 FROM reach WHERE id = NEW.task_id
)
BEGIN
    SELECT RAISE(ABORT, 'dependency_cycle');
END;

-- Edges are inserted or deleted, never rewritten (an UPDATE would bypass the cycle check).
CREATE TRIGGER tr_task_dependency_no_update
BEFORE UPDATE ON task_dependency
BEGIN
    SELECT RAISE(ABORT, 'dependency_immutable');
END;

-- ---------------------------------------------------------------------------------------
-- handoff_note
-- ---------------------------------------------------------------------------------------
-- Caps (scope § 5): summary 800, caveats 800, touched files 1500 characters; the database
-- accepts 10 % more (880 / 880 / 1650). length() counts characters, not bytes.
-- kind: 'handoff' is written by done; 'note' is a free note (e.g. added from the panel).
CREATE TABLE handoff_note (
    id             INTEGER PRIMARY KEY,
    task_id        INTEGER NOT NULL REFERENCES task (id) ON DELETE CASCADE,
    kind           TEXT    NOT NULL DEFAULT 'handoff',
    summary        TEXT    NOT NULL,
    caveats        TEXT,
    touched_files  TEXT,
    commit_hash    TEXT,
    session_id     TEXT,
    taken_over_from TEXT,
    created_at     TEXT    NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),

    CONSTRAINT ck_handoff_note_kind          CHECK (kind IN ('handoff', 'note')),
    CONSTRAINT ck_handoff_note_summary       CHECK (trim(summary) <> '' AND length(summary) <= 880),
    CONSTRAINT ck_handoff_note_caveats       CHECK (caveats IS NULL OR length(caveats) <= 880),
    CONSTRAINT ck_handoff_note_touched_files CHECK (touched_files IS NULL OR length(touched_files) <= 1650)
);

CREATE INDEX ix_handoff_note_task ON handoff_note (task_id, created_at);

-- ---------------------------------------------------------------------------------------
-- decision
-- ---------------------------------------------------------------------------------------
-- Belongs to the project; task_id is only a trace and survives task deletion (SET NULL).
-- Never deleted: a changed decision becomes 'superseded' and points to its replacement.
-- Text cap 400 (+10 % = 440). Rationale has no cap and is never returned by `next`.
CREATE TABLE decision (
    id               INTEGER PRIMARY KEY,
    project_id       INTEGER NOT NULL REFERENCES project (id),
    task_id          INTEGER REFERENCES task (id) ON DELETE SET NULL,
    title            TEXT    NOT NULL,
    text             TEXT    NOT NULL,
    rationale        TEXT,
    status           TEXT    NOT NULL DEFAULT 'active',
    superseded_by_id INTEGER REFERENCES decision (id),
    superseded_at    TEXT,
    session_id       TEXT,
    created_at       TEXT    NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),

    CONSTRAINT ck_decision_status        CHECK (status IN ('active', 'superseded')),
    CONSTRAINT ck_decision_title         CHECK (trim(title) <> ''),
    CONSTRAINT ck_decision_text          CHECK (trim(text) <> '' AND length(text) <= 440),
    CONSTRAINT ck_decision_superseded_by CHECK ((status = 'superseded') = (superseded_by_id IS NOT NULL)),
    CONSTRAINT ck_decision_superseded_at CHECK ((status = 'superseded') = (superseded_at IS NOT NULL)),
    CONSTRAINT ck_decision_not_self      CHECK (superseded_by_id IS NULL OR superseded_by_id <> id)
);

CREATE INDEX ix_decision_project ON decision (project_id, status, id);

CREATE TRIGGER tr_decision_no_delete
BEFORE DELETE ON decision
BEGIN
    SELECT RAISE(ABORT, 'decision_never_deleted');
END;

-- Superseding is one-way, and only by a decision of the same project.
CREATE TRIGGER tr_decision_supersede
BEFORE UPDATE OF status, superseded_by_id ON decision
FOR EACH ROW
BEGIN
    SELECT RAISE(ABORT, 'decision_already_superseded')
    WHERE OLD.status = 'superseded';
    SELECT RAISE(ABORT, 'decision_replacement_other_project')
    WHERE NEW.superseded_by_id IS NOT NULL
      AND (SELECT project_id FROM decision WHERE id = NEW.superseded_by_id) IS NOT NEW.project_id;
END;

-- Decision text is the record; a correction is a new decision that supersedes this one.
CREATE TRIGGER tr_decision_text_immutable
BEFORE UPDATE OF project_id, title, text ON decision
FOR EACH ROW
WHEN NEW.project_id IS NOT OLD.project_id OR NEW.title IS NOT OLD.title OR NEW.text IS NOT OLD.text
BEGIN
    SELECT RAISE(ABORT, 'decision_immutable');
END;

-- ---------------------------------------------------------------------------------------
-- owner_account (ASP.NET Core Identity user store)
-- ---------------------------------------------------------------------------------------
-- Exactly one owner: the singleton column can only hold 1 and is unique. No roles.
CREATE TABLE owner_account (
    id                   TEXT    PRIMARY KEY,
    singleton            INTEGER NOT NULL DEFAULT 1,
    user_name            TEXT    NOT NULL,
    normalized_user_name TEXT    NOT NULL,
    password_hash        TEXT,
    security_stamp       TEXT,
    concurrency_stamp    TEXT,
    lockout_enabled      INTEGER NOT NULL DEFAULT 1,
    lockout_end          TEXT,
    access_failed_count  INTEGER NOT NULL DEFAULT 0,
    created_at           TEXT    NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),

    CONSTRAINT ck_owner_account_singleton     CHECK (singleton = 1),
    CONSTRAINT ck_owner_account_user_name     CHECK (trim(user_name) <> ''),
    CONSTRAINT ck_owner_account_lockout_flag  CHECK (lockout_enabled IN (0, 1)),
    CONSTRAINT ck_owner_account_failed_count  CHECK (access_failed_count >= 0)
) WITHOUT ROWID;

CREATE UNIQUE INDEX ux_owner_account_singleton ON owner_account (singleton);
CREATE UNIQUE INDEX ux_owner_account_user_name ON owner_account (normalized_user_name);

-- ---------------------------------------------------------------------------------------
-- data_protection_key (ASP.NET Core Data Protection key ring)
-- ---------------------------------------------------------------------------------------
-- Keeps panel login cookies valid across restarts without writing files next to the binary.
CREATE TABLE data_protection_key (
    id            INTEGER PRIMARY KEY,
    friendly_name TEXT,
    xml           TEXT NOT NULL,
    created_at    TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now'))
);
