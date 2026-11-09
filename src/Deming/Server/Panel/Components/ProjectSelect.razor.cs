using Deming.Data.Models;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;

namespace Deming.Server.Panel.Components;

/// <summary>
/// The project drop-down. As a filter: active projects (default), all projects, then each project
/// (an inactive one marked). In a form: the active projects only (nothing is added to an inactive one).
/// </summary>
public partial class ProjectSelect
{
    [Inject] private IStringLocalizer<PanelText> L { get; set; } = null!;
    [Parameter, EditorRequired] public string Name { get; set; } = "project";
    [Parameter, EditorRequired] public IReadOnlyList<ProjectSummary> Projects { get; set; } = [];

    /// <summary>The selected value: "", "all" or a project id.</summary>
    [Parameter] public string Value { get; set; } = "";

    /// <summary>True for the list filter, false for the form field.</summary>
    [Parameter] public bool Filter { get; set; }
}
