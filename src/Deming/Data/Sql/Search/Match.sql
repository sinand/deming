-- Search.Match: rows of every searchable kind whose text holds ALL the terms, newest first.
-- Matching is on deming_normalize() of the row's searchable fields joined by a space, so case,
-- Turkish i/ı/İ and diacritics fold the same way as the terms (folded by the caller with the same
-- function). instr() is used instead of LIKE: no wildcard in a term needs escaping.
-- The searched fields come back whole: the caller cuts the snippet and never returns them.
-- Adding a kind: one more UNION ALL branch with the same columns, and its name in SearchTypes.
--
-- Params: @Terms (JSON array of normalized, non-empty terms), @ProjectId (nullable = every
-- project, active or not), @Type (nullable = every kind), @Limit.
-- Columns: type, id, project_id, project_name, title, status, task_id, created_at,
--          field1..field3 (the searchable fields after the title, in the order SearchTypes names them).
SELECT type, id, project_id, project_name, title, status, task_id, created_at, field1, field2, field3
FROM (
    SELECT 'finding' AS type, f.id, f.project_id, p.name AS project_name, f.title,
           NULL AS status, f.task_id, f.created_at,
           f.body AS field1, NULL AS field2, NULL AS field3,
           deming_normalize(f.title || ' ' || f.body) AS haystack
    FROM finding f
    JOIN project p ON p.id = f.project_id
    WHERE (@Type IS NULL OR @Type = 'finding')
      AND (@ProjectId IS NULL OR f.project_id = @ProjectId)

    UNION ALL

    SELECT 'decision', d.id, d.project_id, p.name, d.title,
           d.status, d.task_id, d.created_at,
           d.text, d.rationale, NULL,
           deming_normalize(d.title || ' ' || d.text || ' ' || coalesce(d.rationale, ''))
    FROM decision d
    JOIN project p ON p.id = d.project_id
    WHERE (@Type IS NULL OR @Type = 'decision')
      AND (@ProjectId IS NULL OR d.project_id = @ProjectId)

    UNION ALL

    -- Handoff notes and the notes block/cancel write (their reason is the summary).
    -- A note has no title of its own: it carries its task's.
    SELECT 'note', n.id, t.project_id, p.name, t.title,
           n.kind, n.task_id, n.created_at,
           n.summary, n.caveats, n.touched_files,
           deming_normalize(n.summary || ' ' || coalesce(n.caveats, '') || ' ' || coalesce(n.touched_files, ''))
    FROM handoff_note n
    JOIN task t ON t.id = n.task_id
    JOIN project p ON p.id = t.project_id
    WHERE (@Type IS NULL OR @Type = 'note')
      AND (@ProjectId IS NULL OR t.project_id = @ProjectId)

    UNION ALL

    SELECT 'task', t.id, t.project_id, p.name, t.title,
           t.status, NULL, t.created_at,
           t.scope, NULL, NULL,
           deming_normalize(t.title || ' ' || coalesce(t.scope, ''))
    FROM task t
    JOIN project p ON p.id = t.project_id
    WHERE (@Type IS NULL OR @Type = 'task')
      AND (@ProjectId IS NULL OR t.project_id = @ProjectId)

    UNION ALL

    -- Channel messages (the body only). A message has no title: it carries its sender's label, and
    -- its status column is the channel it was said in ('project' or 'task-<id>'), task_id the task.
    SELECT 'message', m.id, m.project_id, p.name, m.sender,
           CASE WHEN m.task_id IS NULL THEN 'project' ELSE 'task-' || m.task_id END, m.task_id, m.created_at,
           m.body, NULL, NULL,
           deming_normalize(m.body)
    FROM message m
    JOIN project p ON p.id = m.project_id
    WHERE (@Type IS NULL OR @Type = 'message')
      AND (@ProjectId IS NULL OR m.project_id = @ProjectId)
) AS candidate
WHERE NOT EXISTS (SELECT 1 FROM json_each(@Terms) term WHERE instr(candidate.haystack, term.value) = 0)
ORDER BY created_at DESC, type, id DESC
LIMIT @Limit;
