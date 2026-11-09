using Microsoft.AspNetCore.Components;

namespace Deming.Server.Panel.Components;

/// <summary>
/// The old per-project pages (<c>/projects/{id}</c>, <c>/decisions</c>, <c>/findings</c>) now live in
/// the top-level pages with the project filter. A temporary (302) redirect, so a browser never
/// caches the path and it can change again.
/// </summary>
public partial class ProjectRedirect
{
    [CascadingParameter] private HttpContext Http { get; set; } = null!;
    [Parameter] public long ProjectId { get; set; }

    protected override void OnInitialized()
    {
        var path = Http.Request.Path.Value ?? "";
        var page = path.EndsWith("/decisions", StringComparison.Ordinal) ? "decisions"
            : path.EndsWith("/findings", StringComparison.Ordinal) ? "findings"
            : "tasks";
        Http.Response.Redirect($"/{page}?project={ProjectId}", permanent: false);
    }
}
