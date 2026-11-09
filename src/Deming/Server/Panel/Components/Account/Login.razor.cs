using Deming.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;

namespace Deming.Server.Panel.Components.Account;

/// <summary>
/// Password step of the sign-in. Five failures lock the account for 15 minutes;
/// with two-factor on, the next step is <see cref="LoginWith2fa"/>.
/// </summary>
[AllowAnonymous]
public partial class Login
{
    [Inject] private OwnerAccountStore Store { get; set; } = null!;

    [SupplyParameterFromQuery(Name = "ReturnUrl")] public string? ReturnUrl { get; set; }

    private string? _userName;
    private bool _rememberMe;

    protected override async Task OnInitializedAsync()
    {
        // No owner yet: the first visit creates it.
        if (!await Store.OwnerExistsAsync(Http.RequestAborted))
            Nav.NavigateTo("/register");
    }

    private async Task OnPostAsync()
    {
        var form = await FormAsync();
        _userName = Field(form, "username");
        _rememberMe = Checked(form, "rememberMe");

        if (!await HumanAsync(form))
        {
            Fail(L["Err_HumanCheck"]);
            return;
        }

        var result = await SignIn.PasswordSignInAsync(_userName ?? "", Raw(form, "password"), _rememberMe, lockoutOnFailure: true);
        if (result.Succeeded)
            Nav.NavigateTo(PanelAuth.LocalUrl(ReturnUrl));
        else if (result.RequiresTwoFactor)
            Nav.NavigateTo(SecondStep("/login/2fa", ReturnUrl, _rememberMe));
        else if (result.IsLockedOut)
            Nav.NavigateTo("/lockout");
        else
            Fail(L["Err_BadLogin"], StatusCodes.Status401Unauthorized);
    }

    /// <summary>Link to a second-step page that carries the return address and the remember-me choice.</summary>
    internal static string SecondStep(string path, string? returnUrl, bool rememberMe) =>
        $"{path}?ReturnUrl={Uri.EscapeDataString(PanelAuth.LocalUrl(returnUrl))}&RememberMe={(rememberMe ? "true" : "false")}";
}
