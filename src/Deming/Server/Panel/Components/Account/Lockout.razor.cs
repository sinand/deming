using Microsoft.AspNetCore.Authorization;

namespace Deming.Server.Panel.Components.Account;

/// <summary>Shown after too many failed sign-in attempts (5 failures, 15 minutes).</summary>
[AllowAnonymous]
public partial class Lockout
{
    private int Minutes => (int)Users.Options.Lockout.DefaultLockoutTimeSpan.TotalMinutes;
}
