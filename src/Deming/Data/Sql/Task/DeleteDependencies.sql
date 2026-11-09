-- Task.DeleteDependencies: removes every prerequisite edge of a task (update replaces the set).
-- Must run inside a transaction together with Task.InsertDependencies.
-- Params: @Id
DELETE FROM task_dependency WHERE task_id = @Id;
