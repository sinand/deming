using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;

namespace Deming.Server.Panel.Components.Account;

/// <summary>New recovery codes, shown once in the response that generated them.</summary>
public partial class RecoveryCodeList
{
    [Inject] private IStringLocalizer<PanelText> L { get; set; } = null!;
    [Parameter, EditorRequired] public IReadOnlyList<string> Codes { get; set; } = [];
}
