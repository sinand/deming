using Deming.Data;

namespace Deming.Tests.Data;

public class MigrationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Creates_database_in_wal_mode_at_latest_version()
    {
        await using var t = await TempDatabase.CreateAsync(migrate: false);

        var applied = await t.Db.MigrateAsync(Ct);

        Assert.Equal(SqlCatalog.Schema.Count, applied);
        Assert.True(File.Exists(t.Path));
        Assert.Equal("wal", await t.ScalarAsync("PRAGMA journal_mode;"));
        Assert.Equal((long)SqlCatalog.Schema.Count, await t.ScalarAsync("SELECT max(version) FROM schema_version;"));
        Assert.Equal(1L, await t.ScalarAsync("PRAGMA foreign_keys;"));
    }

    [Fact]
    public async Task Second_run_applies_nothing()
    {
        await using var t = await TempDatabase.CreateAsync();
        Assert.Equal(0, await t.Db.MigrateAsync(Ct));
    }

    [Fact]
    public async Task Refuses_database_newer_than_binary()
    {
        await using var t = await TempDatabase.CreateAsync();
        await t.ExecAsync("INSERT INTO schema_version (version, name) VALUES (9999, '9999_future.sql');");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => t.Db.MigrateAsync(Ct));
        Assert.Contains("9999", ex.Message);
    }

    [Theory]
    [InlineData("project")]
    [InlineData("task")]
    [InlineData("task_dependency")]
    [InlineData("handoff_note")]
    [InlineData("decision")]
    [InlineData("finding")]
    [InlineData("owner_account")]
    [InlineData("data_protection_key")]
    public async Task V1_tables_exist(string table)
    {
        await using var t = await TempDatabase.CreateAsync();
        Assert.Equal(1L, await t.ScalarAsync($"SELECT count(*) FROM sqlite_schema WHERE type = 'table' AND name = '{table}';"));
    }

    [Fact]
    public void Schema_scripts_are_numbered_from_one() =>
        Assert.Equal(Enumerable.Range(1, SqlCatalog.Schema.Count), SqlCatalog.Schema.Select(s => s.Version));
}
