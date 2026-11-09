-- HandoffNote.ForNext: the notes `next` / `take` hand back with a task (scope § 5).
-- Two kinds TOGETHER: notes of the tasks this one depends on (source 'dependency', first),
-- then the project's last 3 finished (done) tasks, excluding the task itself and its
-- dependencies (source 'recent', newest first). One note per task: the latest.
-- Explicit projection: this is the shape of the response, not the table row.
-- Params: @TaskId, @ProjectId.
WITH deps AS (
    SELECT depends_on_task_id AS id FROM task_dependency WHERE task_id = @TaskId
),
recent AS (
    SELECT id
    FROM task
    WHERE project_id = @ProjectId
      AND status = 'done'
      AND id <> @TaskId
      AND id NOT IN (SELECT id FROM deps)
    ORDER BY finished_at DESC, id DESC
    LIMIT 3
),
picked AS (
    SELECT id, 'dependency' AS source FROM deps
    UNION ALL
    SELECT id, 'recent' AS source FROM recent
)
SELECT p.source        AS source,
       t.id            AS task_id,
       t.title         AS task_title,
       n.kind          AS kind,
       n.summary       AS summary,
       n.caveats       AS caveats,
       n.touched_files AS touched_files,
       n.commit_hash   AS commit_hash,
       n.created_at    AS created_at
FROM picked p
JOIN task t ON t.id = p.id
JOIN handoff_note n ON n.id = (SELECT max(id) FROM handoff_note WHERE task_id = t.id)
ORDER BY CASE p.source WHEN 'dependency' THEN 0 ELSE 1 END,
         CASE p.source WHEN 'dependency' THEN t.id END,
         t.finished_at DESC, t.id DESC;
