-- Project.Update: edits name, description, repo path and the active flag. NULL parameter = leave
-- the field as it is; an empty @Description or @RepoPath clears it. No row when the project does not exist.
-- Params: @Id, @Name (nullable), @Description (nullable), @RepoPath (nullable), @IsActive (nullable)
UPDATE project
SET name            = coalesce(trim(@Name), name),
    name_normalized = CASE WHEN @Name IS NULL THEN name_normalized ELSE deming_normalize(trim(@Name)) END,
    description     = CASE WHEN @Description IS NULL THEN description ELSE nullif(trim(@Description), '') END,
    repo_path       = CASE WHEN @RepoPath IS NULL THEN repo_path ELSE nullif(trim(@RepoPath), '') END,
    is_active       = coalesce(@IsActive, is_active)
WHERE id = @Id
RETURNING *;
