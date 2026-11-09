-- Task.ExistingIds: which of the given task ids exist in the project (dependency validation).
-- Params: @ProjectId, @Ids (JSON array of integers, e.g. '[3,5]')
SELECT id
FROM task
WHERE project_id = @ProjectId
  AND id IN (SELECT value FROM json_each(@Ids));
