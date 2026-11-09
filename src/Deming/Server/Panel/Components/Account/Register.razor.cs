using Deming.Data.Models;
using Deming.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;

namespace Deming.Server.Panel.Components.Account;

/// <summary>
/// The first visit creates the single owner account and signs it in. Once the
/// owner exists the page is closed: every request goes to the login.
/// </summary>
[AllowAnonymous]
public partial class Register
{
    [Inject] private OwnerAccountStore Store { get; set; } = null!;

    private string? _userName;

    protected override async Task OnInitializedAsync()
    {
        if (await Store.OwnerExistsAsync(Http.RequestAborted))
            Nav.NavigateTo("/login");
    }

    private async Task OnPostAsync()
    {
        var form = await FormAsync();
        _userName = Field(form, "username");

        if (!await HumanAsync(form))
        {
            Fail(L["Err_HumanCheck"]);
            return;
        }
        if (_userName is null)
        {
            Fail(L["Err_UserNameRequired"]);
            return;
        }
        var password = Raw(form, "password");
        if (password != Raw(form, "confirm"))
        {
            Fail(L["Err_PasswordMismatch"]);
            return;
        }

        var owner = new OwnerAccount { UserName = _userName };
        var result = await Users.CreateAsync(owner, password);
        if (!result.Succeeded)
        {
            Fail(Describe(result));
            return;
        }

        await SignIn.SignInAsync(owner, isPersistent: false);
        Nav.NavigateTo("/");
    }
}
