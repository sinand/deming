using Deming.Data.Models;
using Deming.Data.Services;
using Microsoft.AspNetCore.Components;

namespace Deming.Server.Panel.Components;

/// <summary>Decisions of all projects (<c>/decisions</c>): project / status filter in the URL, the rationale on the detail, the add form.</summary>
public partial class DecisionsPage
{
    [Inject] private DecisionService Decisions { get; set; } = null!;

    [SupplyParameterFromQuery(Name = "status")] public string? Status { get; set; }
    [SupplyParameterFromQuery(Name = "view")] public long? View { get; set; }

    private bool _notFound;
    private IReadOnlyList<DecisionOverviewItem> _items = [];
    private Decision? _selected;
    private string? _selectedProject;

    // Posted values, kept when the form is refused.
    private string? _title;
    private string? _text;
    private string? _rationale;
    private long? _formProject;

    /// <summary>True when a supersede was posted and refused: its form stays open with the values.</summary>
    private bool _supersedeRefused;

    protected override async Task OnInitializedAsync()
    {
        if (!await LoadProjectsAsync())
        {
            _notFound = true;
            return;
        }

        Status = Status is DecisionStatus.Active or DecisionStatus.Superseded ? Status : null;
        _items = await Decisions.ListAllAsync(SelectedId, ActiveOnly, Status, Http.RequestAborted);

        if (View is { } id)
        {
            // The rationale is not in the list (it is fetched by id, scope § 5); a missing decision
            // is simply not shown. The detail is not limited by the project filter (the link may
            // come from a task or a finding).
            try
            {
                _selected = await Decisions.GetAsync(id, Http.RequestAborted);
                _selectedProject = Projects.FirstOrDefault(p => p.Id == _selected.ProjectId)?.Name;
            }
            catch (TaskFlowException ex) when (ex.Error == TaskFlowError.NotFound)
            {
                _selected = null;
            }
        }
    }

    /// <summary>Handles a posted form (all forms are named; Blazor runs this after initialization).
    /// On success the browser is redirected; on a refusal the page renders again with the error.</summary>
    private async Task OnPostAsync()
    {
        var form = await FormAsync();
        _title = Field(form, "title");
        _text = Field(form, "text");
        _rationale = Field(form, "rationale");

        switch (Field(form, "op"))
        {
            case "add":
            {
                _formProject = long.TryParse(Field(form, "projectId"), out var pid) ? pid : null;
                long? taskId = long.TryParse(Field(form, "taskId"), out var t) ? t : null;
                if (_formProject is not { } projectId)
                    Error = L["Err_ProjectRequired"];
                else if (await TryAsync(async () => await Decisions.AddAsync(projectId, PanelSession, Request(taskId))))
                    Done(Link("/decisions", ("project", projectId.ToString())), "DecisionAdded");
                break;
            }

            case "supersede" when long.TryParse(Field(form, "id"), out var id):
            {
                // The text of a decision is immutable (schema trigger): correcting it is a new
                // decision that supersedes the old one, in one transaction.
                SupersedeResult? result = null;
                if (await TryAsync(async () => result = await Decisions.SupersedeAsync(id, PanelSession, Request(null))))
                    Done(Link("/decisions", ("project", result!.Replacement.ProjectId.ToString()), ("view", result.Replacement.Id.ToString())),
                        "DecisionSuperseded");
                else
                    _supersedeRefused = true;
                break;
            }
        }
    }

    private DecisionRequest Request(long? taskId) => new()
    {
        Title = _title ?? "", Text = _text ?? "", Rationale = _rationale, TaskId = taskId,
    };

    private string ViewLink(long id) =>
        Link("/decisions", ("project", ProjectValue), ("status", Status), ("view", id.ToString())) + "#decision";

    private string ClearUrl => Link("/decisions", ("project", ProjectValue));
}
