using System.Globalization;
using Deming.Data;
using Deming.Data.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;

namespace Deming.Server.Panel.Components;

/// <summary>
/// Shared plumbing of the panel pages. A page handles its own form post in OnInitializedAsync
/// (post → act → redirect with <c>?ok=key</c>), so no JavaScript and no circuit are involved.
/// </summary>
public abstract class PanelPage : ComponentBase
{
    /// <summary>Session id the panel acts under (lifecycle calls need one; it never holds a lock).</summary>
    public const string PanelSession = "panel";

    [Inject] protected IStringLocalizer<PanelText> L { get; set; } = null!;
    [Inject] protected NavigationManager Nav { get; set; } = null!;
    [Inject] protected TaskLifecycleService Lifecycle { get; set; } = null!;
    [Inject] protected IOptions<TaskLockOptions> LockOptions { get; set; } = null!;
    [Inject] protected TimeProvider Clock { get; set; } = null!;
    [CascadingParameter] protected HttpContext Http { get; set; } = null!;

    /// <summary>Key of a success message (resource <c>Ok_&lt;key&gt;</c>) left by the redirect after a post.</summary>
    [SupplyParameterFromQuery(Name = "ok")] public string? Ok { get; set; }

    /// <summary>Localized error of the action that just failed; the page renders again with it.</summary>
    protected string? Error { get; set; }

    protected bool IsPost => HttpMethods.IsPost(Http.Request.Method);

    protected string? OkText
    {
        get
        {
            if (Ok is null) return null;
            var text = L["Ok_" + Ok];
            return text.ResourceNotFound ? null : text.Value;
        }
    }

    protected Task<IFormCollection> FormAsync() => Http.Request.ReadFormAsync(Http.RequestAborted);

    /// <summary>Runs a data call; a refusal becomes <see cref="Error"/>. Returns whether it went through.</summary>
    protected async Task<bool> TryAsync(Func<Task> action)
    {
        try
        {
            await action();
            return true;
        }
        catch (TaskFlowException ex)
        {
            Error = ErrorText(ex);
        }
        catch (ConstraintViolationException ex)
        {
            Error = ErrorText(ex);
        }
        return false;
    }

    /// <summary>Redirect (post-redirect-get) to <paramref name="url"/> with a success message.</summary>
    protected void Done(string url, string okKey) =>
        Nav.NavigateTo(url + (url.Contains('?') ? '&' : '?') + "ok=" + Uri.EscapeDataString(okKey));

    private string ErrorText(TaskFlowException ex)
    {
        var text = ex.ExistingTaskId is { } existing ? L["Err_" + ex.Error, existing] : L["Err_" + ex.Error];
        return text.ResourceNotFound ? ex.Message : text.Value;
    }

    private string ErrorText(ConstraintViolationException ex)
    {
        // Rules live in the database; the panel only names the violated kind.
        var text = ex.Kind == ConstraintKind.Trigger ? L["Err_trigger_" + ex.Constraint] : L["Err_Constraint_" + ex.Kind];
        return text.ResourceNotFound ? L["Err_Constraint_Other"].Value : text.Value;
    }

    /// <summary>
    /// Cancels a task. A live lock of another session is released first (requeue with force): the
    /// owner of the panel is the human who may overrule an agent. Both steps are single transactions.
    /// </summary>
    protected async Task CancelAsync(long taskId, string reason)
    {
        try
        {
            await Lifecycle.CancelAsync(taskId, PanelSession, reason);
        }
        catch (TaskFlowException ex) when (ex.Error == TaskFlowError.LockedByOtherSession)
        {
            await Lifecycle.RequeueAsync(taskId, PanelSession, force: true);
            await Lifecycle.CancelAsync(taskId, PanelSession, reason);
        }
    }

    protected bool LockExpired(DateTime? lockedAt) =>
        lockedAt is { } at && at < Clock.GetUtcNow().UtcDateTime - LockOptions.Value.Timeout;

    protected static string Stamp(DateTime? utc) =>
        utc is { } v ? DateTime.SpecifyKind(v, DateTimeKind.Utc).ToLocalTime().ToString("g", CultureInfo.CurrentCulture) : "";

    protected string StatusLabel(string status) => L["Status_" + status].Value;

    protected string PriorityLabel(int priority) => L["Priority_" + priority].Value;

    /// <summary>The text of a form field, trimmed; null when absent or blank.</summary>
    protected static string? Field(IFormCollection form, string name) =>
        form.TryGetValue(name, out var v) && !string.IsNullOrWhiteSpace(v.ToString()) ? v.ToString() : null;

    protected static List<long> Ids(IFormCollection form, string name) =>
        form.TryGetValue(name, out var v)
            ? v.Select(s => long.TryParse(s, out var id) ? id : 0).Where(id => id > 0).Distinct().ToList()
            : [];
}
