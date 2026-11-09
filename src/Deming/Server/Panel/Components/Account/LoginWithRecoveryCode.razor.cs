using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;

namespace Deming.Server.Panel.Components.Account;

/// <summary>Second step without the device: a single-use recovery code instead of the TOTP code.</summary>
[AllowAnonymous]
public partial class LoginWithRecoveryCode
{
    [SupplyParameterFromQuery(Name = "ReturnUrl")] public string? ReturnUrl { get; set; }

    protected override async Task OnInitializedAsync()
    {
        if (await SignIn.GetTwoFactorAuthenticationUserAsync() is null)
            Nav.NavigateTo("/login");
    }

    private async Task OnPostAsync()
    {
        var form = await FormAsync();
        // The store ignores case, spaces and dashes.
        var result = await SignIn.TwoFactorRecoveryCodeSignInAsync(Field(form, "code") ?? "");
        // Identity signs in with a session cookie here: a recovery code is an emergency, not a
        // device to remember, so the remember-me choice of the password step does not apply.
        if (result.Succeeded)
            Nav.NavigateTo(PanelAuth.LocalUrl(ReturnUrl));
        else if (result.IsLockedOut)
            Nav.NavigateTo("/lockout");
        else
            Fail(L["Err_BadRecoveryCode"], StatusCodes.Status401Unauthorized);
    }
}
