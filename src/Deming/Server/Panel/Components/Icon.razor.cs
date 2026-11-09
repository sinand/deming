using Microsoft.AspNetCore.Components;

namespace Deming.Server.Panel.Components;

/// <summary>
/// A stroke icon drawn inline (no icon font, no request). Decorative only: the text next to it,
/// or an aria-label on the link, carries the meaning.
/// </summary>
public partial class Icon
{
    private static readonly Dictionary<string, string> Paths = new(StringComparer.Ordinal)
    {
        ["projects"] = "M12 3 3 7.5l9 4.5 9-4.5L12 3Z M3 12l9 4.5 9-4.5 M3 16.5 12 21l9-4.5",
        ["decision"] = "M12 3v18 M5 21h14 M12 6 4 8l-2 7a4 4 0 0 0 4 0l-2-7 M12 6l8 2 2 7a4 4 0 0 1-4 0l2-7",
        ["finding"] = "M10.5 18a7.5 7.5 0 1 0 0-15 7.5 7.5 0 0 0 0 15Z M16 16l5 5",
        ["account"] = "M12 12a4 4 0 1 0 0-8 4 4 0 0 0 0 8Z M4 20c1.5-3.5 4.5-5 8-5s6.5 1.5 8 5",
        ["sun"] = "M12 16a4 4 0 1 0 0-8 4 4 0 0 0 0 8Z M12 2v2 M12 20v2 M4.9 4.9l1.4 1.4 M17.7 17.7l1.4 1.4 M2 12h2 M20 12h2 M4.9 19.1l1.4-1.4 M17.7 6.3l1.4-1.4",
        ["moon"] = "M20 14.5A8 8 0 0 1 9.5 4 8 8 0 1 0 20 14.5Z",
        ["system"] = "M3 5h18v11H3z M8 20h8 M12 16v4",
        ["empty"] = "M3 13h5l1.5 3h5l1.5-3h5 M5.5 5h13L21 13v6H3v-6l2.5-8Z",
        ["list"] = "M8 6h12 M8 12h12 M8 18h12 M4 6h.01 M4 12h.01 M4 18h.01",
        ["edit"] = "M4 20h4L19 9l-4-4L4 16v4Z M13.5 6.5l4 4",
        ["pause"] = "M12 21a9 9 0 1 0 0-18 9 9 0 0 0 0 18Z M10 9v6 M14 9v6",
        ["play"] = "M12 21a9 9 0 1 0 0-18 9 9 0 0 0 0 18Z M10 8.5v7l6-3.5-6-3.5Z",
        ["plus"] = "M12 5v14 M5 12h14",
        ["sort-up"] = "M7 14l5-5 5 5",
        ["sort-down"] = "M7 10l5 5 5-5",
        ["signout"] ="M9 4H5v16h4 M14 8l4 4-4 4 M18 12H9",
    };

    [Parameter, EditorRequired] public string Name { get; set; } = "";

    private string Path => Paths.TryGetValue(Name, out var d) ? d : "";
}
