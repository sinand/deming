using Deming.Data.Models;

namespace Deming.Server.Panel.Components.Account;

/// <summary>
/// Two-step sign-in while it is on: new recovery codes, forget this browser, turn it off, or reset
/// the authenticator key (which turns it off until the app is set up again).
/// </summary>
public partial class TwoFactor
{
    public const int RecoveryCodeCount = 10;

    private OwnerAccount? _owner;
    private int _codesLeft;
    private bool _browserRemembered;
    private IReadOnlyList<string>? _codes;

    protected override async Task OnInitializedAsync()
    {
        _owner = await OwnerAsync();
        if (_owner is null)
            return;
        _codesLeft = await Users.CountRecoveryCodesAsync(_owner);
        _browserRemembered = await SignIn.IsTwoFactorClientRememberedAsync(_owner);
    }

    private async Task GenerateCodesAsync()
    {
        if (_owner is not { TwoFactorEnabled: true })
            return;
        // Shown once in this response; only their hashes are stored.
        _codes = [.. await Users.GenerateNewTwoFactorRecoveryCodesAsync(_owner, RecoveryCodeCount) ?? []];
    }

    private async Task ForgetBrowserAsync()
    {
        await SignIn.ForgetTwoFactorClientAsync();
        Done("/account/2fa", "BrowserForgotten");
    }

    private async Task DisableAsync()
    {
        if (_owner is null)
            return;
        var result = await Users.SetTwoFactorEnabledAsync(_owner, false);
        if (!result.Succeeded)
        {
            Fail(Describe(result));
            return;
        }
        await SignIn.RefreshSignInAsync(_owner); // a new security stamp: keep this session
        Done("/account", "TwoFactorDisabled");
    }

    private async Task ResetAsync()
    {
        if (_owner is null)
            return;
        var result = await Users.SetTwoFactorEnabledAsync(_owner, false);
        if (result.Succeeded)
            result = await Users.ResetAuthenticatorKeyAsync(_owner);
        if (!result.Succeeded)
        {
            Fail(Describe(result));
            return;
        }
        await SignIn.RefreshSignInAsync(_owner);
        Done("/account/2fa/enable", "AuthenticatorReset");
    }
}
