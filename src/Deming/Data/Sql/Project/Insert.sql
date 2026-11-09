-- Project.Insert: creates a project. Duplicate normalized name -> UNIQUE (ux_project_name).
-- Params: @Name, @Description, @RepoPath (both nullable; blank = none)
INSERT INTO project (name, name_normalized, description, repo_path)
VALUES (trim(@Name), deming_normalize(trim(@Name)), nullif(trim(@Description), ''), nullif(trim(@RepoPath), ''))
RETURNING *;
