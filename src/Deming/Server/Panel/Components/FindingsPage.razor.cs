using Deming.Data.Models;
using Deming.Data.Services;
using Microsoft.AspNetCore.Components;

namespace Deming.Server.Panel.Components;

/// <summary>Findings of all projects (<c>/findings</c>): project filter in the URL, the body on the detail, the add form.</summary>
public partial class FindingsPage
{
    [Inject] private FindingService Findings { get; set; } = null!;

    [SupplyParameterFromQuery(Name = "view")] public long? View { get; set; }

    private bool _notFound;
    private IReadOnlyList<FindingOverviewItem> _items = [];
    private Finding? _selected;
    private string? _selectedProject;

    // Posted values, kept when the form is refused.
    private string? _title;
    private string? _body;
    private string? _taskId;
    private string? _decisionId;
    private long? _formProject;

    protected override async Task OnInitializedAsync()
    {
        if (!await LoadProjectsAsync())
        {
            _notFound = true;
            return;
        }

        _items = await Findings.ListAllAsync(SelectedId, ActiveOnly, Http.RequestAborted);

        if (View is { } id)
        {
            // The list carries no body; it is fetched by id. A missing finding is not shown.
            try
            {
                _selected = await Findings.GetAsync(id, Http.RequestAborted);
                _selectedProject = Projects.FirstOrDefault(p => p.Id == _selected.ProjectId)?.Name;
            }
            catch (TaskFlowException ex) when (ex.Error == TaskFlowError.NotFound)
            {
                _selected = null;
            }
        }
    }

    /// <summary>Handles the add form. On success the browser is redirected to the project's findings.</summary>
    private async Task OnPostAsync()
    {
        var form = await FormAsync();
        _title = Field(form, "title");
        _body = Field(form, "body");
        _taskId = Field(form, "taskId");
        _decisionId = Field(form, "decisionId");
        _formProject = long.TryParse(Field(form, "projectId"), out var pid) ? pid : null;

        if (_formProject is not { } projectId)
        {
            Error = L["Err_ProjectRequired"];
            return;
        }
        // A trace that is not a number is a mistake, not "no trace".
        if ((_taskId is not null && !long.TryParse(_taskId, out _)) || (_decisionId is not null && !long.TryParse(_decisionId, out _)))
        {
            Error = L["Err_TraceNotNumber"];
            return;
        }

        var request = new FindingRequest
        {
            Title = _title ?? "", Body = _body ?? "",
            TaskId = _taskId is null ? null : long.Parse(_taskId),
            DecisionId = _decisionId is null ? null : long.Parse(_decisionId),
        };
        if (await TryAsync(async () => await Findings.AddAsync(projectId, PanelSession, request)))
            Done(Link("/findings", ("project", projectId.ToString())), "FindingAdded");
    }

    private string ViewLink(long id) => Link("/findings", ("project", ProjectValue), ("view", id.ToString())) + "#finding";
}
