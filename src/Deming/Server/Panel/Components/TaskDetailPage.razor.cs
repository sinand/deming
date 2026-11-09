using Deming.Data.Models;
using Deming.Data.Services;
using Microsoft.AspNetCore.Components;

namespace Deming.Server.Panel.Components;

public partial class TaskDetailPage
{
    [Inject] private ProjectService ProjectsData { get; set; } = null!;
    [Inject] private TaskQueryService Tasks { get; set; } = null!;

    [Parameter] public long TaskId { get; set; }

    private TaskDetail? _detail;
    private Project? _project;
    private IReadOnlyList<TaskListItem> _candidates = [];
    private HashSet<long> _selected = [];

    // Posted values, kept when a save is refused.
    private string? _title;
    private string? _scope;
    private int? _priority;

    protected override async Task OnInitializedAsync()
    {
        try
        {
            _detail = await Tasks.GetAsync(TaskId, Http.RequestAborted);
        }
        catch (TaskFlowException ex) when (ex.Error == TaskFlowError.NotFound)
        {
            Http.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        _project = await ProjectsData.GetByIdAsync(_detail.Task.ProjectId, Http.RequestAborted);
        _candidates = (await Tasks.ListAsync(_detail.Task.ProjectId, null, Http.RequestAborted))
            .Where(t => t.Id != TaskId).ToList();
        _selected = _detail.Dependencies.Select(d => d.Id).ToHashSet();
    }

    /// <summary>Handles a posted form (all forms are named; Blazor runs this after initialization).
    /// On success the browser is redirected; on a refusal the page renders again with the error.</summary>
    private async Task OnPostAsync()
    {
        var form = await FormAsync();
        switch (Field(form, "op"))
        {
            case "save":
                _title = Field(form, "title");
                // A blank scope is a deliberate clear: send "" rather than null (null = leave alone).
                _scope = form["scope"].ToString();
                _priority = int.TryParse(Field(form, "priority"), out var priority) ? priority : null;
                var deps = Ids(form, "dependsOn");
                _selected = deps.ToHashSet(); // show what was posted if the save is refused
                if (_title is null)
                    Error = L["Err_TitleRequired"];
                else if (await TryAsync(async () => await Lifecycle.UpdateAsync(TaskId, null, new UpdateTaskRequest
                {
                    Title = _title, Scope = _scope, Priority = _priority, DependsOn = deps,
                })))
                    Done($"/tasks/{TaskId}", "TaskSaved");
                break;

            case "release":
                if (await TryAsync(async () => await Lifecycle.RequeueAsync(TaskId, PanelSession, force: true)))
                    Done($"/tasks/{TaskId}", "TaskRequeued");
                break;

            case "cancel":
                if (await TryAsync(() => CancelAsync(TaskId, Field(form, "reason") ?? "")))
                    Done($"/tasks/{TaskId}", "TaskCancelled");
                break;
        }
    }
}
