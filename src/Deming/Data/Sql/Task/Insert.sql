-- Task.Insert: adds a queued task at the end of the project's order (max sort_order + 10).
-- The normalized title is computed here; the partial UNIQUE index ux_task_open_title is the
-- last line of defence against duplicates. Must run inside a transaction.
-- Params: @ProjectId, @Title, @Scope (nullable), @Priority, @Source, @AddedBySession (nullable)
INSERT INTO task (project_id, sort_order, title, title_normalized, scope, priority, source, added_by_session)
VALUES (
    @ProjectId,
    (SELECT coalesce(max(sort_order), 0) + 10 FROM task WHERE project_id = @ProjectId),
    trim(@Title),
    deming_normalize(@Title),
    nullif(trim(@Scope), ''),
    @Priority,
    @Source,
    @AddedBySession)
RETURNING *;
