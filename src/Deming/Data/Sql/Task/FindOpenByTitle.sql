-- Task.FindOpenByTitle: an unfinished task (queued, in progress, blocked) of the project whose
-- normalized title equals the normalized @Title. Same predicate as ux_task_open_title.
-- Params: @ProjectId, @Title, @ExcludeId (task being edited; 0 when adding)
SELECT *
FROM task
WHERE project_id = @ProjectId
  AND title_normalized = deming_normalize(@Title)
  AND status IN ('queued', 'in_progress', 'blocked')
  AND id <> @ExcludeId
ORDER BY id
LIMIT 1;
