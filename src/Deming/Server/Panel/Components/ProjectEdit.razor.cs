using Deming.Data.Models;
using Deming.Data.Services;
using Microsoft.AspNetCore.Components;

namespace Deming.Server.Panel.Components;

/// <summary>Add a project (<c>/projects/new</c>) or edit one (<c>/projects/{id}/edit</c>). Projects are never deleted.</summary>
public partial class ProjectEdit
{
    [Inject] private ProjectService ProjectsData { get; set; } = null!;

    [Parameter] public long? ProjectId { get; set; }

    private Project? _project;
    private string? _name;
    private string? _description;
    private string? _repoPath;
    private bool _active = true;

    private bool IsNew => ProjectId is null;

    protected override async Task OnInitializedAsync()
    {
        if (IsNew)
            return;
        _project = await ProjectsData.GetByIdAsync(ProjectId!.Value, Http.RequestAborted);
        if (_project is null)
        {
            Http.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        _name = _project.Name;
        _description = _project.Description;
        _repoPath = _project.RepoPath;
        _active = _project.IsActive;
    }

    private async Task OnPostAsync()
    {
        var form = await FormAsync();
        _name = Field(form, "name")?.Trim();
        _description = Field(form, "description");
        _repoPath = Field(form, "repoPath")?.Trim();
        if (_name is null)
        {
            Error = L["Err_NameRequired"];
            return;
        }

        if (IsNew)
        {
            if (await TryAsync(async () => await ProjectsData.CreateAsync(_name, _description, _repoPath)))
                Done("/projects", "ProjectCreated");
            return;
        }

        // An empty description or repo path clears it (the update treats "" as clear, null as keep).
        _active = Field(form, "active") == "1";
        var changes = new ProjectChanges(_name, _description ?? "", _repoPath ?? "", _active);
        if (await TryAsync(async () => await ProjectsData.UpdateAsync(ProjectId!.Value, changes)))
            Done("/projects", "ProjectSaved");
    }
}
