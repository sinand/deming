using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;

namespace Deming.Server.Panel.Components;

/// <summary>
/// The page frame. Signed in: sidebar with the main navigation, a top bar with the preferences and
/// sign-out, the page in the content column. On the sign-in pages: a centred card instead.
/// </summary>
public partial class Shell
{
    [Inject] private IStringLocalizer<PanelText> L { get; set; } = null!;
    [CascadingParameter] private HttpContext Http { get; set; } = null!;
    [Parameter] public RenderFragment? ChildContent { get; set; }

    /// <summary>False on the sign-in pages: no menu and no sign-out, only the preferences.</summary>
    [Parameter] public bool SignedIn { get; set; } = true;

    /// <summary>A narrow single-column page (the account forms).</summary>
    [Parameter] public bool Narrow { get; set; }

    private string? UserName => Http.User.Identity?.IsAuthenticated == true ? Http.User.Identity.Name : null;

    private string Initial => UserName is { Length: > 0 } name ? name[..1] : "";

    /// <summary>Which sidebar entry the current page belongs to; "/" is the project list. Null when none does.</summary>
    private string? Section
    {
        get
        {
            var path = Http.Request.Path;
            if (path == "/")
                return "projects";
            foreach (var section in new[] { "tasks", "projects", "decisions", "findings", "account" })
                if (path.StartsWithSegments("/" + section))
                    return section;
            return null;
        }
    }
}
