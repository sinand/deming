using Microsoft.AspNetCore.Components;

namespace Deming.Server.Panel.Components;

public partial class App
{
    [CascadingParameter] private HttpContext Http { get; set; } = null!;

    /// <summary>"light" or "dark" when the owner picked one; null follows the system setting.</summary>
    private string? Theme => PanelHost.Theme(Http);
}
