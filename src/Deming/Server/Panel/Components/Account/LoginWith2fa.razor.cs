using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;

namespace Deming.Server.Panel.Components.Account;

/// <summary>
/// Second step: the TOTP code of the authenticator app. Only reachable with the short-lived
/// two-factor cookie the password step leaves; a wrong code counts toward the lockout.
/// </summary>
[AllowAnonymous]
public partial class LoginWith2fa
{
    [SupplyParameterFromQuery(Name = "ReturnUrl")] public string? ReturnUrl { get; set; }
    [SupplyParameterFromQuery(Name = "RememberMe")] public bool RememberMe { get; set; }

    private string RecoveryLink => Login.SecondStep("/login/recovery", ReturnUrl, RememberMe);

    protected override async Task OnInitializedAsync()
    {
        if (await SignIn.GetTwoFactorAuthenticationUserAsync() is null)
            Nav.NavigateTo("/login");
    }

    private async Task OnPostAsync()
    {
        var form = await FormAsync();
        var code = (Field(form, "code") ?? "").Replace(" ", "", StringComparison.Ordinal).Replace("-", "", StringComparison.Ordinal);

        var result = await SignIn.TwoFactorAuthenticatorSignInAsync(code, RememberMe, Checked(form, "rememberMachine"));
        if (result.Succeeded)
            Nav.NavigateTo(PanelAuth.LocalUrl(ReturnUrl));
        else if (result.IsLockedOut)
            Nav.NavigateTo("/lockout");
        else
            Fail(L["Err_BadCode"], StatusCodes.Status401Unauthorized);
    }
}
