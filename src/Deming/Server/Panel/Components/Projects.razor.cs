using Deming.Data.Models;
using Deming.Data.Services;
using Microsoft.AspNetCore.Components;

namespace Deming.Server.Panel.Components;

public partial class Projects
{
    [Inject] private ProjectService ProjectsData { get; set; } = null!;

    /// <summary>Column to sort by: <c>id</c> or <c>name</c> (default). Plain links, no script.</summary>
    [SupplyParameterFromQuery(Name = "sort")] public string? SortParam { get; set; }

    [SupplyParameterFromQuery(Name = "dir")] public string? DirParam { get; set; }

    private List<ProjectSummary> _projects = [];

    private string Sort => SortParam == "id" ? "id" : "name";
    private bool Descending => DirParam == "desc";

    protected override async Task OnInitializedAsync()
    {
        var all = await ProjectsData.ListAsync(Http.RequestAborted);
        // The server lists by normalized name; a sort by name keeps that order (diacritics-aware).
        _projects = Sort == "id"
            ? (Descending ? all.OrderByDescending(p => p.Id) : all.OrderBy(p => p.Id)).ToList()
            : (Descending ? all.AsEnumerable().Reverse() : all).ToList();
    }

    /// <summary>The link of a column header: the same column flips the direction, another starts ascending.</summary>
    private string SortUrl(string column) =>
        $"/projects?sort={column}&dir={(Sort == column && !Descending ? "desc" : "asc")}";

    private string? AriaSort(string column) =>
        Sort != column ? null : Descending ? "descending" : "ascending";

    /// <summary>The row form that switches a project between active and inactive.</summary>
    private async Task OnPostAsync()
    {
        var form = await FormAsync();
        if (!long.TryParse(Field(form, "id"), out var id))
            return;
        var activate = Field(form, "active") == "1";
        if (await TryAsync(async () => await ProjectsData.UpdateAsync(id, new ProjectChanges(IsActive: activate))))
            Done($"/projects?sort={Sort}&dir={(Descending ? "desc" : "asc")}", activate ? "ProjectActivated" : "ProjectDeactivated");
    }
}
