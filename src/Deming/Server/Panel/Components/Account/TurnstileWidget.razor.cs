using Deming.Server.Exposure;
using Microsoft.AspNetCore.Components;

namespace Deming.Server.Panel.Components.Account;

/// <summary>The Cloudflare Turnstile widget of the anonymous forms; renders nothing when Turnstile is off.</summary>
public partial class TurnstileWidget
{
    [Inject] private TurnstileOptions Options { get; set; } = null!;
}
