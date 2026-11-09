using Deming.Data;
using Deming.Data.Models;
using Deming.Data.Services;
using Microsoft.AspNetCore.Components;

namespace Deming.Server.Panel.Components;

/// <summary>Tasks of all projects (<c>/tasks</c>): project / status filter and search in the URL, the add form.</summary>
public partial class TasksPage
{
    [Inject] private TaskQueryService Tasks { get; set; } = null!;

    [SupplyParameterFromQuery(Name = "status")] public string? Status { get; set; }
    [SupplyParameterFromQuery(Name = "q")] public string? Q { get; set; }

    /// <summary>The status filter's "ready to take" choice: not a status, the tasks next could take now ('deming parallel').</summary>
    private const string EligibleFilter = "eligible";

    private bool _notFound;
    private List<TaskOverviewItem> _shown = [];
    private List<TaskListItem> _open = [];

    // Values of the add form, kept when it is refused.
    private string? _title;
    private string? _scope;
    private int _priority = TaskPriority.Default;
    private List<long> _dependsOn = [];
    private long? _formProject;

    protected override async Task OnInitializedAsync()
    {
        if (!await LoadProjectsAsync())
        {
            _notFound = true;
            return;
        }

        Status = Status is { } s && (TaskItemStatus.All.Contains(s) || s == EligibleFilter) ? s : null;
        Q = string.IsNullOrWhiteSpace(Q) ? null : Q.Trim();

        var eligible = Status == EligibleFilter;
        var all = await Tasks.ListAllAsync(SelectedId, ActiveOnly, eligible ? null : Status, eligible, Http.RequestAborted);
        var needle = Q is null ? null : TextNormalizer.Normalize(Q);
        _shown = all
            .Where(t => needle is null
                || TextNormalizer.Normalize(t.Title).Contains(needle, StringComparison.Ordinal)
                || t.Id.ToString() == needle.TrimStart('#'))
            .ToList();

        // Dependencies can be picked once the view is one project (the new task joins that project).
        if (SelectedId is { } projectId)
        {
            var everything = await Tasks.ListAsync(projectId, null, Http.RequestAborted);
            _open = everything.Where(t => t.Status is not (TaskItemStatus.Done or TaskItemStatus.Cancelled)).ToList();
        }
    }

    /// <summary>Handles a posted form (all forms are named; Blazor runs this after initialization).
    /// On success the browser is redirected; on a refusal the page renders again with the error.</summary>
    private async Task OnPostAsync()
    {
        var form = await FormAsync();
        switch (Field(form, "op"))
        {
            case "add":
                _title = Field(form, "title");
                _scope = Field(form, "scope");
                _priority = int.TryParse(Field(form, "priority"), out var priority) ? priority : TaskPriority.Default;
                _dependsOn = Ids(form, "dependsOn");
                _formProject = long.TryParse(Field(form, "projectId"), out var pid) ? pid : null;
                if (_formProject is not { } projectId)
                    Error = L["Err_ProjectRequired"];
                else if (_title is null)
                    Error = L["Err_TitleRequired"];
                else if (await TryAsync(async () => await Lifecycle.AddAsync(projectId, PanelSession, new AddTaskRequest
                {
                    Title = _title, Scope = _scope, Priority = _priority,
                    Source = TaskSource.Human, DependsOn = _dependsOn,
                })))
                    Done(Link("/tasks", ("project", projectId.ToString())), "TaskAdded");
                break;

            case "release" when long.TryParse(Field(form, "id"), out var releaseId):
                if (await TryAsync(async () => await Lifecycle.RequeueAsync(releaseId, PanelSession, force: true)))
                    Done(Back(), "TaskRequeued");
                break;

            case "cancel" when long.TryParse(Field(form, "id"), out var cancelId):
                if (await TryAsync(() => CancelAsync(cancelId, Field(form, "reason") ?? "")))
                    Done(Back(), "TaskCancelled");
                break;
        }
    }

    /// <summary>The list as it was (with its filter), so a row action does not lose the view.</summary>
    private string Back() => Link("/tasks", ("project", ProjectValue), ("status", Status), ("q", Q));

    private string ClearUrl => Link("/tasks", ("project", ProjectValue));
}
