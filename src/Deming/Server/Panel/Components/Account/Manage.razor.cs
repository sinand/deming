using Deming.Data.Models;

namespace Deming.Server.Panel.Components.Account;

/// <summary>The owner's account: two-factor status and the password change.</summary>
public partial class Manage
{
    private OwnerAccount? _owner;

    protected override async Task OnInitializedAsync() => _owner = await OwnerAsync();

    private async Task OnPostAsync()
    {
        if (_owner is null)
            return;
        var form = await FormAsync();
        var password = Raw(form, "password");
        if (password != Raw(form, "confirm"))
        {
            Fail(L["Err_PasswordMismatch"]);
            return;
        }

        var result = await Users.ChangePasswordAsync(_owner, Raw(form, "current"), password);
        if (!result.Succeeded)
        {
            Fail(Describe(result));
            return;
        }

        // The new security stamp signs every other session out; this one gets a fresh cookie.
        await SignIn.RefreshSignInAsync(_owner);
        Done("/account", "PasswordChanged");
    }
}
