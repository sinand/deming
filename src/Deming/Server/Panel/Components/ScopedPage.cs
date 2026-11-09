using Deming.Data.Models;
using Deming.Data.Services;
using Microsoft.AspNetCore.Components;

namespace Deming.Server.Panel.Components;

/// <summary>
/// A top-level list page (Tasks, Decisions, Findings) that spans projects. The project filter is
/// the query value <c>project</c>: absent = the active projects, <c>all</c> = every project
/// (inactive ones too), a number = that one project (even an inactive one). The filter lives in the
/// URL so a view can be shared and the row actions can come back to it.
/// </summary>
public abstract class ScopedPage : PanelPage
{
    public const string AllProjects = "all";

    [Inject] protected ProjectService ProjectsData { get; set; } = null!;

    [SupplyParameterFromQuery(Name = "project")] public string? ProjectParam { get; set; }

    /// <summary>Every project (for the filter and the add form), by name.</summary>
    protected IReadOnlyList<ProjectSummary> Projects { get; private set; } = [];

    /// <summary>The single project the view is limited to; null for the active / all views.</summary>
    protected ProjectSummary? Selected { get; private set; }

    protected long? SelectedId => Selected?.Id;

    /// <summary>True when the view shows the active projects only (no <c>project</c> value).</summary>
    protected bool ActiveOnly => Selected is null && ProjectParam != AllProjects;

    /// <summary>The normalized value of the filter: "", "all" or the project id.</summary>
    protected string ProjectValue => Selected is { } p ? p.Id.ToString() : ProjectParam == AllProjects ? AllProjects : "";

    /// <summary>Loads the projects and resolves the filter. False (and a 404) when the project does not exist.</summary>
    protected async Task<bool> LoadProjectsAsync()
    {
        Projects = await ProjectsData.ListAsync(Http.RequestAborted);
        if (!string.IsNullOrWhiteSpace(ProjectParam) && ProjectParam != AllProjects)
        {
            Selected = long.TryParse(ProjectParam, out var id) ? Projects.FirstOrDefault(p => p.Id == id) : null;
            if (Selected is null)
            {
                Http.Response.StatusCode = StatusCodes.Status404NotFound;
                return false;
            }
        }
        return true;
    }

    /// <summary>The active projects (what the add forms offer).</summary>
    protected IEnumerable<ProjectSummary> ActiveProjects => Projects.Where(p => p.IsActive);

    /// <summary>A link to <paramref name="path"/> with the non-empty values as the query string, encoded.</summary>
    protected static string Link(string path, params (string Key, string? Value)[] query)
    {
        var parts = query.Where(q => !string.IsNullOrEmpty(q.Value))
            .Select(q => q.Key + "=" + Uri.EscapeDataString(q.Value!));
        var joined = string.Join('&', parts);
        return joined.Length == 0 ? path : path + "?" + joined;
    }
}
