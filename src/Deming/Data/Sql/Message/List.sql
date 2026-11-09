-- Message.List: messages after a cursor, oldest first. Ids become visible in id order (one
-- writer, id assigned inside the write transaction), so "id > cursor" never skips one.
-- Params: @ProjectId; @TaskId (nullable = the project channel); @All (1 = every channel of the
-- project, @TaskId ignored); @After (cursor, 0 = from the start); @To (nullable: only what is
-- addressed to this label, plus what is addressed to everyone except the label's own messages,
-- so a writer does not wake its own wait); @Take (rows to return; the caller asks one more than
-- it shows to learn whether more is waiting).
SELECT id,
       project_id,
       task_id,
       sender,
       sender_session_id,
       recipient,
       reply_to_id,
       body,
       created_at
FROM message
WHERE project_id = @ProjectId
  AND (@All = 1 OR task_id IS @TaskId)
  AND id > @After
  AND (@To IS NULL
       OR recipient = lower(trim(@To))
       OR (recipient IS NULL AND sender <> lower(trim(@To))))
ORDER BY id
LIMIT @Take;
