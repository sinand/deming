using Microsoft.AspNetCore.Components;

namespace Deming.Server.Panel.Components;

/// <summary>Priority as a three-bar meter next to its label (1 high fills three bars, 3 low one).</summary>
public partial class Prio
{
    [Parameter, EditorRequired] public int Priority { get; set; }
    [Parameter, EditorRequired] public string Label { get; set; } = "";
}
