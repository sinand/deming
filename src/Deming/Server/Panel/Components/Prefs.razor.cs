using System.Globalization;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;

namespace Deming.Server.Panel.Components;

/// <summary>
/// Language and theme switches. Plain links to /culture and /theme: each sets a preference cookie
/// and redirects back here, so no script is involved.
/// </summary>
public partial class Prefs
{
    [Inject] private IStringLocalizer<PanelText> L { get; set; } = null!;
    [CascadingParameter] private HttpContext Http { get; set; } = null!;

    private static string Culture => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;

    private string Theme => PanelHost.Theme(Http) ?? "system";

    private string Back => Uri.EscapeDataString(Http.Request.Path + Http.Request.QueryString);

    private string CultureLink(string culture) => $"/culture?c={culture}&r={Back}";

    private string ThemeLink(string theme) => $"/theme?t={theme}&r={Back}";
}
