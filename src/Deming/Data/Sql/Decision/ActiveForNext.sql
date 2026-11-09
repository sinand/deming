-- Decision.ActiveForNext: active decisions of the project as `next` / `take` return them (scope § 5).
-- Text is cut at 250 characters with truncated = 1; rationale is NOT selected (fetched by id).
-- Explicit projection: this is the shape of the response, not the table row.
-- Params: @ProjectId, @TextLimit.
SELECT id,
       title,
       substr(text, 1, @TextLimit) AS text,
       length(text) > @TextLimit   AS truncated,
       task_id,
       created_at
FROM decision
WHERE project_id = @ProjectId
  AND status = 'active'
ORDER BY id;
