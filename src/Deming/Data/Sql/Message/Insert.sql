-- Message.Insert: writes a message. Labels are lowercased here; their shape, the body cap, the
-- same-project task, the read-only finished channel and the same-channel reply are schema rules
-- (CHECK and triggers) and are not repeated in C#. The body is not returned.
-- Params: @ProjectId, @TaskId (nullable), @Sender, @SessionId (nullable), @Recipient (nullable,
-- blank = everyone), @ReplyToId (nullable), @Body
INSERT INTO message (project_id, task_id, sender, sender_session_id, recipient, reply_to_id, body)
VALUES (@ProjectId, @TaskId, lower(trim(@Sender)), @SessionId, nullif(lower(trim(coalesce(@Recipient, ''))), ''),
        @ReplyToId, trim(@Body))
RETURNING id, project_id, task_id, sender, sender_session_id, recipient, reply_to_id, created_at;
