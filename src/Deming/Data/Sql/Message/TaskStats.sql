-- Message.TaskStats: what a task channel holds when the task is closed (information for the
-- handoff note, never a reason to refuse). A question is not marked, so the count is an estimate:
-- a message addressed to an agent or holding a '?', that is not itself a reply and has no reply.
-- Params: @TaskId
SELECT count(*) AS message_count,
       coalesce(sum(CASE
           WHEN m.reply_to_id IS NULL
            AND (m.recipient IS NOT NULL OR instr(m.body, '?') > 0)
            AND NOT EXISTS (SELECT 1 FROM message r WHERE r.reply_to_id = m.id)
           THEN 1 ELSE 0 END), 0) AS unanswered_question_count
FROM message m
WHERE m.task_id = @TaskId;
