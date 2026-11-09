-- Task.InsertDependencies: @Id waits for every id in @Ids. Triggers reject a cycle
-- ('dependency_cycle') and a prerequisite of another project ('dependency_other_project').
-- Params: @Id, @Ids (JSON array of integers)
INSERT INTO task_dependency (task_id, depends_on_task_id)
SELECT @Id, value FROM json_each(@Ids);
