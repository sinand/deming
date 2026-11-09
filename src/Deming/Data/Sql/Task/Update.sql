-- Task.Update: edits title, scope and priority. NULL parameter = leave the field as it is;
-- an empty @Scope clears the scope. Status is never touched here.
-- Params: @Id, @Title (nullable), @Scope (nullable), @Priority (nullable)
UPDATE task
SET title            = coalesce(trim(@Title), title),
    title_normalized = CASE WHEN @Title IS NULL THEN title_normalized ELSE deming_normalize(@Title) END,
    scope            = CASE WHEN @Scope IS NULL THEN scope ELSE nullif(trim(@Scope), '') END,
    priority         = coalesce(@Priority, priority)
WHERE id = @Id
RETURNING *;
