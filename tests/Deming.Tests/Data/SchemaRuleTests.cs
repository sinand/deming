using Deming.Data;

namespace Deming.Tests.Data;

/// <summary>The rules the database enforces on its own (CHECK, partial UNIQUE, triggers).</summary>
public class SchemaRuleTests
{
    private const string Now = "strftime('%Y-%m-%dT%H:%M:%fZ','now')";

    private static async Task<TempDatabase> WithProjectsAsync()
    {
        var t = await TempDatabase.CreateAsync();
        await t.ExecAsync("""
            INSERT INTO project (id, name, name_normalized) VALUES (1, 'Alpha', 'alpha'), (2, 'Beta', 'beta');
            INSERT INTO task (id, project_id, sort_order, title, title_normalized) VALUES
                (1, 1, 10, 'One', 'one'), (2, 1, 20, 'Two', 'two'), (3, 1, 30, 'Three', 'three'),
                (9, 2, 10, 'Other', 'other');
            """);
        return t;
    }

    private static async Task<ConstraintViolationException> Rejects(TempDatabase t, string sql) =>
        await Assert.ThrowsAsync<ConstraintViolationException>(() => t.ExecAsync(sql));

    // --- task ---

    [Fact]
    public async Task Status_is_checked_text()
    {
        await using var t = await WithProjectsAsync();
        var ex = await Rejects(t, "UPDATE task SET status = 'started' WHERE id = 1;");
        Assert.True(ex.Is(ConstraintKind.Check, "ck_task_status"));
    }

    [Fact]
    public async Task In_progress_requires_a_lock_owner()
    {
        await using var t = await WithProjectsAsync();
        var ex = await Rejects(t, "UPDATE task SET status = 'in_progress' WHERE id = 1;");
        Assert.True(ex.Is(ConstraintKind.Check, "ck_task_lock_only_in_progress"));
    }

    [Fact]
    public async Task Lock_owner_only_while_in_progress()
    {
        await using var t = await WithProjectsAsync();
        var ex = await Rejects(t, $"UPDATE task SET lock_owner = 's1', locked_at = {Now} WHERE id = 1;");
        Assert.True(ex.Is(ConstraintKind.Check, "ck_task_lock_only_in_progress"));
    }

    [Fact]
    public async Task Lock_owner_and_adding_session_are_separate()
    {
        await using var t = await WithProjectsAsync();
        await t.ExecAsync("UPDATE task SET added_by_session = 's-add' WHERE id = 1;");
        await t.ExecAsync($"UPDATE task SET status = 'in_progress', lock_owner = 's-lock', locked_at = {Now} WHERE id = 1;");
        Assert.Equal("s-add|s-lock", await t.ScalarAsync("SELECT added_by_session || '|' || lock_owner FROM task WHERE id = 1;"));
    }

    [Fact]
    public async Task Lock_needs_a_time()
    {
        await using var t = await WithProjectsAsync();
        var ex = await Rejects(t, "UPDATE task SET status = 'in_progress', lock_owner = 's1' WHERE id = 1;");
        Assert.True(ex.Is(ConstraintKind.Check, "ck_task_lock_has_time"));
    }

    [Fact]
    public async Task Blocked_and_cancelled_need_reasons()
    {
        await using var t = await WithProjectsAsync();
        Assert.True((await Rejects(t, "UPDATE task SET status = 'blocked' WHERE id = 1;"))
            .Is(ConstraintKind.Check, "ck_task_blocked_has_reason"));
        Assert.True((await Rejects(t, $"UPDATE task SET status = 'cancelled', finished_at = {Now}, cancelled_reason = '  ' WHERE id = 1;"))
            .Is(ConstraintKind.Check, "ck_task_cancelled_has_reason"));
    }

    [Fact]
    public async Task Done_needs_finish_time()
    {
        await using var t = await WithProjectsAsync();
        Assert.True((await Rejects(t, "UPDATE task SET status = 'done' WHERE id = 1;"))
            .Is(ConstraintKind.Check, "ck_task_finished_has_time"));
    }

    [Fact]
    public async Task Duplicate_open_title_rejected_but_finished_title_reusable()
    {
        await using var t = await WithProjectsAsync();

        var ex = await Rejects(t, "INSERT INTO task (project_id, sort_order, title, title_normalized) VALUES (1, 40, 'ONE', 'one');");
        Assert.Equal(ConstraintKind.Unique, ex.Kind);

        // Same title in another project is fine.
        await t.ExecAsync("INSERT INTO task (project_id, sort_order, title, title_normalized) VALUES (2, 40, 'One', 'one');");

        // Once the task is done, the title may be used again.
        await t.ExecAsync($"UPDATE task SET status = 'done', finished_at = {Now} WHERE id = 1;");
        await t.ExecAsync("INSERT INTO task (project_id, sort_order, title, title_normalized) VALUES (1, 40, 'One', 'one');");
    }

    [Fact]
    public async Task Task_cannot_move_between_projects()
    {
        await using var t = await WithProjectsAsync();
        var ex = await Rejects(t, "UPDATE task SET project_id = 2 WHERE id = 1;");
        Assert.True(ex.Is(ConstraintKind.Trigger, "task_project_immutable"));
    }

    [Fact]
    public async Task Updated_at_moves_on_update()
    {
        await using var t = await WithProjectsAsync();
        await t.ExecAsync("UPDATE task SET updated_at = '2000-01-01T00:00:00.000Z' WHERE id = 1;");
        await t.ExecAsync("UPDATE task SET scope = 'x' WHERE id = 1;");
        var updated = (string?)await t.ScalarAsync("SELECT updated_at FROM task WHERE id = 1;");
        Assert.StartsWith(DateTime.UtcNow.Year.ToString(), updated);
    }

    // --- dependencies ---

    [Fact]
    public async Task Dependency_cycle_rejected()
    {
        await using var t = await WithProjectsAsync();
        await t.ExecAsync("INSERT INTO task_dependency (task_id, depends_on_task_id) VALUES (2, 1), (3, 2);");

        var ex = await Rejects(t, "INSERT INTO task_dependency (task_id, depends_on_task_id) VALUES (1, 3);");
        Assert.True(ex.Is(ConstraintKind.Trigger, "dependency_cycle"));
    }

    [Fact]
    public async Task Self_dependency_rejected()
    {
        await using var t = await WithProjectsAsync();
        var ex = await Rejects(t, "INSERT INTO task_dependency (task_id, depends_on_task_id) VALUES (1, 1);");
        // The cycle trigger runs before the CHECK; either way the row is refused.
        Assert.True(ex.Is(ConstraintKind.Trigger, "dependency_cycle") || ex.Is(ConstraintKind.Check, "ck_task_dependency_not_self"));
    }

    [Fact]
    public async Task Dependency_across_projects_rejected()
    {
        await using var t = await WithProjectsAsync();
        var ex = await Rejects(t, "INSERT INTO task_dependency (task_id, depends_on_task_id) VALUES (1, 9);");
        Assert.True(ex.Is(ConstraintKind.Trigger, "dependency_other_project"));
    }

    [Fact]
    public async Task Dependency_on_missing_task_rejected()
    {
        await using var t = await WithProjectsAsync();
        var ex = await Rejects(t, "INSERT INTO task_dependency (task_id, depends_on_task_id) VALUES (1, 404);");
        Assert.True(ex.Kind is ConstraintKind.ForeignKey or ConstraintKind.Trigger);
    }

    // --- handoff notes ---

    [Fact]
    public async Task Note_caps_allow_ten_percent_and_no_more()
    {
        await using var t = await WithProjectsAsync();
        await t.ExecAsync("INSERT INTO handoff_note (task_id, summary, caveats, touched_files) VALUES (1, printf('%.880c', 'a'), printf('%.880c', 'b'), printf('%.1650c', 'c'));");

        Assert.True((await Rejects(t, "INSERT INTO handoff_note (task_id, summary) VALUES (1, printf('%.881c', 'a'));"))
            .Is(ConstraintKind.Check, "ck_handoff_note_summary"));
        Assert.True((await Rejects(t, "INSERT INTO handoff_note (task_id, summary, caveats) VALUES (1, 'ok', printf('%.881c', 'a'));"))
            .Is(ConstraintKind.Check, "ck_handoff_note_caveats"));
        Assert.True((await Rejects(t, "INSERT INTO handoff_note (task_id, summary, touched_files) VALUES (1, 'ok', printf('%.1651c', 'a'));"))
            .Is(ConstraintKind.Check, "ck_handoff_note_touched_files"));
    }

    [Fact]
    public async Task Note_cap_counts_characters_not_bytes()
    {
        await using var t = await WithProjectsAsync();
        // 880 two-byte characters = 1760 bytes, still within the cap.
        await t.ExecAsync("INSERT INTO handoff_note (task_id, summary) VALUES (1, replace(printf('%.880c', 'x'), 'x', 'ü'));");
    }

    [Fact]
    public async Task Empty_summary_rejected()
    {
        await using var t = await WithProjectsAsync();
        Assert.True((await Rejects(t, "INSERT INTO handoff_note (task_id, summary) VALUES (1, '   ');"))
            .Is(ConstraintKind.Check, "ck_handoff_note_summary"));
    }

    // --- decisions ---

    [Fact]
    public async Task Decisions_are_never_deleted_and_survive_task_deletion()
    {
        await using var t = await WithProjectsAsync();
        await t.ExecAsync("INSERT INTO decision (id, project_id, task_id, title, text) VALUES (1, 1, 1, 'T', 'Use SQLite');");

        Assert.True((await Rejects(t, "DELETE FROM decision WHERE id = 1;")).Is(ConstraintKind.Trigger, "decision_never_deleted"));

        await t.ExecAsync("DELETE FROM task WHERE id = 1;");
        Assert.Equal(1L, await t.ScalarAsync("SELECT count(*) FROM decision WHERE id = 1 AND task_id IS NULL;"));
    }

    [Fact]
    public async Task Supersede_points_to_replacement_in_same_project_once()
    {
        await using var t = await WithProjectsAsync();
        await t.ExecAsync("""
            INSERT INTO decision (id, project_id, title, text) VALUES
                (1, 1, 'A', 'old'), (2, 1, 'A', 'new'), (3, 2, 'B', 'elsewhere'), (4, 1, 'C', 'newer');
            """);

        Assert.True((await Rejects(t, "UPDATE decision SET status = 'superseded' WHERE id = 1;"))
            .Is(ConstraintKind.Check, "ck_decision_superseded_by"));
        Assert.True((await Rejects(t, $"UPDATE decision SET status = 'superseded', superseded_by_id = 3, superseded_at = {Now} WHERE id = 1;"))
            .Is(ConstraintKind.Trigger, "decision_replacement_other_project"));

        await t.ExecAsync($"UPDATE decision SET status = 'superseded', superseded_by_id = 2, superseded_at = {Now} WHERE id = 1;");

        Assert.True((await Rejects(t, "UPDATE decision SET superseded_by_id = 4 WHERE id = 1;"))
            .Is(ConstraintKind.Trigger, "decision_already_superseded"));
    }

    [Fact]
    public async Task Decision_text_is_capped_and_immutable()
    {
        await using var t = await WithProjectsAsync();
        Assert.True((await Rejects(t, "INSERT INTO decision (project_id, title, text) VALUES (1, 'T', printf('%.441c', 'a'));"))
            .Is(ConstraintKind.Check, "ck_decision_text"));

        await t.ExecAsync("INSERT INTO decision (id, project_id, title, text, rationale) VALUES (1, 1, 'T', 'text', 'why');");
        Assert.True((await Rejects(t, "UPDATE decision SET text = 'changed' WHERE id = 1;")).Is(ConstraintKind.Trigger, "decision_immutable"));
        await t.ExecAsync("UPDATE decision SET rationale = 'more why' WHERE id = 1;");
    }

    // --- findings ---

    [Fact]
    public async Task Finding_title_and_body_are_checked()
    {
        await using var t = await WithProjectsAsync();
        Assert.True((await Rejects(t, "INSERT INTO finding (project_id, title, body) VALUES (1, '  ', 'b');")).Is(ConstraintKind.Check, "ck_finding_title"));
        Assert.True((await Rejects(t, "INSERT INTO finding (project_id, title, body) VALUES (1, printf('%.201c', 'a'), 'b');")).Is(ConstraintKind.Check, "ck_finding_title"));
        Assert.True((await Rejects(t, "INSERT INTO finding (project_id, title, body) VALUES (1, 'T', '');")).Is(ConstraintKind.Check, "ck_finding_body"));
        Assert.True((await Rejects(t, "INSERT INTO finding (project_id, title, body) VALUES (1, 'T', printf('%.11001c', 'a'));")).Is(ConstraintKind.Check, "ck_finding_body"));
        await t.ExecAsync("INSERT INTO finding (project_id, title, body) VALUES (1, printf('%.200c', 'a'), printf('%.11000c', 'a'));");
    }

    [Fact]
    public async Task Finding_traces_stay_in_the_project_and_the_record_is_permanent()
    {
        await using var t = await WithProjectsAsync();
        await t.ExecAsync("INSERT INTO decision (id, project_id, title, text) VALUES (1, 1, 'D', 'x'), (2, 2, 'E', 'y');");

        Assert.True((await Rejects(t, "INSERT INTO finding (project_id, task_id, title, body) VALUES (1, 9, 'T', 'b');"))
            .Is(ConstraintKind.Trigger, "finding_task_other_project"));
        Assert.True((await Rejects(t, "INSERT INTO finding (project_id, decision_id, title, body) VALUES (1, 2, 'T', 'b');"))
            .Is(ConstraintKind.Trigger, "finding_decision_other_project"));

        await t.ExecAsync("INSERT INTO finding (id, project_id, task_id, decision_id, title, body) VALUES (1, 1, 1, 1, 'T', 'b');");
        Assert.True((await Rejects(t, "DELETE FROM finding WHERE id = 1;")).Is(ConstraintKind.Trigger, "finding_never_deleted"));
        Assert.True((await Rejects(t, "UPDATE finding SET body = 'changed' WHERE id = 1;")).Is(ConstraintKind.Trigger, "finding_immutable"));
        Assert.True((await Rejects(t, "DELETE FROM decision WHERE id = 1;")).Is(ConstraintKind.Trigger, "decision_never_deleted"));

        // deleting the task only clears the trace
        await t.ExecAsync("DELETE FROM task WHERE id = 1;");
        Assert.Equal(1L, await t.ScalarAsync("SELECT count(*) FROM finding WHERE id = 1 AND task_id IS NULL AND decision_id = 1;"));
    }

    // --- project / owner ---

    [Fact]
    public async Task Project_name_unique_after_normalization()
    {
        await using var t = await WithProjectsAsync();
        Assert.Equal(ConstraintKind.Unique,
            (await Rejects(t, "INSERT INTO project (name, name_normalized) VALUES ('ALPHA', deming_normalize('ALPHA'));")).Kind);
    }

    [Fact]
    public async Task Only_one_owner_account()
    {
        await using var t = await TempDatabase.CreateAsync();
        await t.ExecAsync("INSERT INTO owner_account (id, user_name, normalized_user_name) VALUES ('a', 'me', 'ME');");
        Assert.Equal(ConstraintKind.Unique,
            (await Rejects(t, "INSERT INTO owner_account (id, user_name, normalized_user_name) VALUES ('b', 'you', 'YOU');")).Kind);
        Assert.True((await Rejects(t, "INSERT INTO owner_account (id, singleton, user_name, normalized_user_name) VALUES ('c', 2, 'x', 'X');"))
            .Is(ConstraintKind.Check, "ck_owner_account_singleton"));
    }
}
