using System.ComponentModel.DataAnnotations.Schema;
using Deming.Data;
using Deming.Data.Models;
using Deming.Data.Services;

namespace Deming.Tests.Data;

public class DataLayerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Every_embedded_query_follows_the_naming_rule()
    {
        Assert.NotEmpty(SqlCatalog.Queries);
        Assert.Contains("Project.Insert", SqlCatalog.Queries.Keys);
        Assert.Contains("OwnerAccount.Update", SqlCatalog.Queries.Keys);
    }

    [Fact]
    public async Task Every_embedded_query_compiles_against_the_schema()
    {
        await using var t = await TempDatabase.CreateAsync();
        await using var c = await t.Db.OpenConnectionAsync(Ct);
        foreach (var (name, sql) in SqlCatalog.Queries)
        {
            await using var cmd = c.CreateCommand();
            cmd.CommandText = sql;
            var ex = Record.Exception(() => cmd.Prepare());
            Assert.True(ex is null, $"{name}: {ex?.Message}");
        }
    }

    [Fact]
    public void Unknown_query_name_names_the_expected_file()
    {
        var db = new DemingDb(TempDatabase.NewPath());
        var ex = Assert.Throws<KeyNotFoundException>(() => db.Sql("Task.DoesNotExist"));
        Assert.Contains("Data/Sql/Task/DoesNotExist.sql", ex.Message);
    }

    [Fact]
    public void Parameter_names_are_given_without_prefix()
    {
        var db = new DemingDb(TempDatabase.NewPath());
        Assert.Throws<ArgumentException>(() => db.Sql("Project.GetById").AddParameter("@Id", 1));
        Assert.Throws<ArgumentException>(() => db.Sql("Project.GetById").AddParameter("Id", 1).AddParameter("id", 2));
    }

    [Fact]
    public async Task Missing_parameter_fails_instead_of_binding_null()
    {
        await using var t = await TempDatabase.CreateAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => t.Db.Sql("Project.GetById").GetModelAsync<Project>(Ct));
    }

    [Fact]
    public async Task Maps_snake_case_columns_timestamps_and_returning()
    {
        await using var t = await TempDatabase.CreateAsync();
        var before = DateTime.UtcNow.AddSeconds(-5);

        var project = await t.Db.Sql("Project.Insert")
            .AddParameter("Name", "  Köprü  ")
            .AddParameter("Description", null).AddParameter("RepoPath", null)
            .GetModelAsync<Project>(Ct);

        Assert.NotNull(project);
        Assert.True(project.Id > 0);
        Assert.Equal("Köprü", project.Name);
        Assert.Equal("kopru", project.NameNormalized);
        Assert.Null(project.Description);
        Assert.Equal(DateTimeKind.Utc, project.CreatedAt.Kind);
        Assert.InRange(project.CreatedAt, before, DateTime.UtcNow.AddSeconds(5));
    }

    private sealed class Renamed
    {
        [Column("name_normalized")]
        public string Key { get; set; } = "";

        [NotMapped]
        public string Name { get; set; } = "untouched";
    }

    [Fact]
    public async Task Column_and_NotMapped_attributes_are_honoured()
    {
        await using var t = await TempDatabase.CreateAsync();
        await new ProjectService(t.Db).CreateAsync("Alpha", ct: Ct);

        var rows = await t.Db.Sql("Project.List").GetModelListAsync<Renamed>(Ct);

        var row = Assert.Single(rows);
        Assert.Equal("alpha", row.Key);
        Assert.Equal("untouched", row.Name);
    }

    [Fact]
    public async Task Scalar_reads_first_column()
    {
        await using var t = await TempDatabase.CreateAsync();
        Assert.False(await t.Db.Sql("OwnerAccount.Exists").GetScalarAsync<bool>(Ct));
    }

    [Fact]
    public async Task Transaction_rolls_back_when_the_delegate_throws()
    {
        await using var t = await TempDatabase.CreateAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => t.Db.InTransactionAsync(async scope =>
        {
            await scope.Sql("Project.Insert").AddParameter("Name", "Alpha").AddParameter("Description", null).AddParameter("RepoPath", null).ExecuteAsync(Ct);
            throw new InvalidOperationException("business rule");
        }, Ct));

        Assert.Empty(await new ProjectService(t.Db).ListAsync(Ct));
    }

    [Fact]
    public async Task Transaction_commits_all_statements()
    {
        await using var t = await TempDatabase.CreateAsync();

        var count = await t.Db.InTransactionAsync(async scope =>
        {
            await scope.Sql("Project.Insert").AddParameter("Name", "Alpha").AddParameter("Description", null).AddParameter("RepoPath", null).ExecuteAsync(Ct);
            await scope.Sql("Project.Insert").AddParameter("Name", "Beta").AddParameter("Description", "b").AddParameter("RepoPath", null).ExecuteAsync(Ct);
            return (await scope.Sql("Project.List").GetModelListAsync<Project>(Ct)).Count;
        }, Ct);

        Assert.Equal(2, count);
        Assert.Equal(2, (await new ProjectService(t.Db).ListAsync(Ct)).Count);
    }

    [Fact]
    public async Task Constraint_violation_surfaces_with_query_name()
    {
        await using var t = await TempDatabase.CreateAsync();
        var projects = new ProjectService(t.Db);
        await projects.CreateAsync("Köprü", ct: Ct);

        var ex = await Assert.ThrowsAsync<ConstraintViolationException>(() => projects.CreateAsync("KOPRU", ct: Ct));

        Assert.Equal("Project.Insert", ex.Query);
        Assert.Equal(ConstraintKind.Unique, ex.Kind);
    }

    [Fact]
    public async Task Project_found_by_any_spelling()
    {
        await using var t = await TempDatabase.CreateAsync();
        var projects = new ProjectService(t.Db);
        var created = await projects.CreateAsync("İş Takibi", "desc", ct: Ct);

        var found = await projects.GetByNameAsync("is takibi", Ct);

        Assert.NotNull(found);
        Assert.Equal(created.Id, found.Id);
        Assert.Equal("desc", found.Description);
        Assert.Null(await projects.GetByNameAsync("missing", Ct));
    }
}
