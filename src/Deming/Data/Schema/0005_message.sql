-- Deming schema 0005: the message channel between agents.
-- A message is coordination in the moment, not a record for the next session: it is never part
-- of the `next` response, hands over no lock and grants no right. What must outlive the moment
-- goes into a handoff note, a decision or a finding when the task is closed.
--
-- There is no channel object: the task is the channel, and task_id NULL is the project channel.
-- The address is an agent label, not a session (sessions die, labels stay); recipient NULL means
-- everyone. The server keeps no read state: a reader keeps its own cursor (the last id it saw)
-- and asks for what came after it.
--
-- Cursor safety: a reader that has seen id N must never later find a new message below N.
--   * SQLite has one writer at a time and the id is assigned inside the write transaction, so
--     ids become visible in id order (no "11 visible before 10").
--   * AUTOINCREMENT: an id is never reused, even when the newest messages go away with their task.
--
-- Labels are lowercase ASCII (letters, digits, '.', '_', '-'), at most 64 characters. The insert
-- lowercases them, so "Codex" and "codex" are one agent; anything else (a Turkish letter, a space)
-- is refused instead of silently never matching.
-- Body cap: 2000 characters, the database accepts 10 % more (2200), like the other caps.

CREATE TABLE message (
    id                INTEGER PRIMARY KEY AUTOINCREMENT,
    project_id        INTEGER NOT NULL REFERENCES project (id),
    task_id           INTEGER REFERENCES task (id) ON DELETE CASCADE,
    sender            TEXT    NOT NULL,
    sender_session_id TEXT,
    recipient         TEXT,
    reply_to_id       INTEGER REFERENCES message (id) ON DELETE CASCADE,
    body              TEXT    NOT NULL,
    created_at        TEXT    NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),

    CONSTRAINT ck_message_sender
        CHECK (sender <> '' AND length(sender) <= 64 AND sender NOT GLOB '*[^a-z0-9._-]*'),
    CONSTRAINT ck_message_recipient
        CHECK (recipient IS NULL OR (recipient <> '' AND length(recipient) <= 64 AND recipient NOT GLOB '*[^a-z0-9._-]*')),
    CONSTRAINT ck_message_body
        CHECK (trim(body) <> '' AND length(body) <= 2200),
    CONSTRAINT ck_message_not_reply_to_self
        CHECK (reply_to_id IS NULL OR reply_to_id <> id)
);

-- Reading a channel after a cursor; the project-wide read ("all channels") uses the second.
CREATE INDEX ix_message_channel ON message (project_id, task_id, id);
CREATE INDEX ix_message_project ON message (project_id, id);
CREATE INDEX ix_message_reply   ON message (reply_to_id) WHERE reply_to_id IS NOT NULL;

-- The task channel belongs to the message's own project (an unknown task fails here too).
CREATE TRIGGER tr_message_task_same_project
BEFORE INSERT ON message
FOR EACH ROW
WHEN NEW.task_id IS NOT NULL
 AND (SELECT project_id FROM task WHERE id = NEW.task_id) IS NOT NEW.project_id
BEGIN
    SELECT RAISE(ABORT, 'message_task_other_project');
END;

-- The channel of a done or cancelled task is read-only; it is kept, not deleted.
CREATE TRIGGER tr_message_task_finished
BEFORE INSERT ON message
FOR EACH ROW
WHEN NEW.task_id IS NOT NULL
 AND (SELECT status FROM task WHERE id = NEW.task_id) IN ('done', 'cancelled')
BEGIN
    SELECT RAISE(ABORT, 'message_task_finished');
END;

-- A reply stays in the channel of the message it answers (an unknown message fails here too).
CREATE TRIGGER tr_message_reply_same_channel
BEFORE INSERT ON message
FOR EACH ROW
WHEN NEW.reply_to_id IS NOT NULL
 AND NOT EXISTS (
     SELECT 1 FROM message
     WHERE id = NEW.reply_to_id
       AND project_id = NEW.project_id
       AND task_id IS NEW.task_id)
BEGIN
    SELECT RAISE(ABORT, 'message_reply_other_channel');
END;

-- What was said stays said; a correction is a new message. The only way a message goes is with
-- its task (ON DELETE CASCADE), like the task's handoff notes.
CREATE TRIGGER tr_message_immutable
BEFORE UPDATE ON message
BEGIN
    SELECT RAISE(ABORT, 'message_immutable');
END;
