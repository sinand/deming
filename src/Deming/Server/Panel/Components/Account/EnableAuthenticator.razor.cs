using System.Text;
using Deming.Data.Models;

namespace Deming.Server.Panel.Components.Account;

/// <summary>
/// Adds an authenticator app: shows the otpauth URI as a QR code, plus the shared key and the URI as text,
/// checks one code from the app, turns two-step sign-in on and shows fresh recovery codes once.
/// </summary>
public partial class EnableAuthenticator
{
    public const string Issuer = "Deming";

    private OwnerAccount? _owner;
    private string? _key;
    private string? _uri;
    private string? _qr;
    private IReadOnlyList<string>? _codes;

    protected override async Task OnInitializedAsync()
    {
        _owner = await OwnerAsync();
        if (_owner is null)
            return;

        var key = await Users.GetAuthenticatorKeyAsync(_owner);
        if (string.IsNullOrEmpty(key))
        {
            await Users.ResetAuthenticatorKeyAsync(_owner);
            // The reset changes the security stamp; keep this session signed in.
            await SignIn.RefreshSignInAsync(_owner);
            key = await Users.GetAuthenticatorKeyAsync(_owner) ?? "";
        }

        _key = FormatKey(key);
        _uri = AuthenticatorUri(_owner.UserName, key);
        // The SVG is built here from the matrix and the localized label only; nothing user-typed is markup.
        _qr = QrCode.ToSvg(QrCode.Encode(_uri), L["EnableQrLabel"]);
    }

    private async Task OnPostAsync()
    {
        if (_owner is null)
            return;
        var form = await FormAsync();
        var code = (Field(form, "code") ?? "").Replace(" ", "", StringComparison.Ordinal).Replace("-", "", StringComparison.Ordinal);

        if (!await Users.VerifyTwoFactorTokenAsync(_owner, Users.Options.Tokens.AuthenticatorTokenProvider, code))
        {
            Fail(L["Err_BadCode"]);
            return;
        }

        var result = await Users.SetTwoFactorEnabledAsync(_owner, true);
        if (!result.Succeeded)
        {
            Fail(Describe(result));
            return;
        }
        await SignIn.RefreshSignInAsync(_owner);
        // New codes every time two-step sign-in is turned on; old ones (if any) stop working.
        _codes = [.. await Users.GenerateNewTwoFactorRecoveryCodesAsync(_owner, TwoFactor.RecoveryCodeCount) ?? []];
    }

    /// <summary>Groups of four, lower case: easier to type into an app by hand.</summary>
    internal static string FormatKey(string key)
    {
        var result = new StringBuilder();
        for (var i = 0; i < key.Length; i += 4)
        {
            if (i > 0) result.Append(' ');
            result.Append(key.AsSpan(i, Math.Min(4, key.Length - i)));
        }
        return result.ToString().ToLowerInvariant();
    }

    /// <summary>Key URI format of authenticator apps (otpauth://totp/Issuer:account?secret=...&amp;issuer=...).</summary>
    internal static string AuthenticatorUri(string userName, string key) =>
        $"otpauth://totp/{Uri.EscapeDataString(Issuer)}:{Uri.EscapeDataString(userName)}" +
        $"?secret={key}&issuer={Uri.EscapeDataString(Issuer)}&digits=6";
}
