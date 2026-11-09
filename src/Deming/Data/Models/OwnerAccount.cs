namespace Deming.Data.Models;

/// <summary>
/// Row of table <c>owner_account</c>: the single panel owner, the TUser of ASP.NET Core Identity.
/// No roles, no e-mail, no external logins; two-factor with an authenticator app.
/// </summary>
public sealed class OwnerAccount
{
    public string Id { get; set; } = Guid.NewGuid().ToString("D");
    public string UserName { get; set; } = "";
    public string NormalizedUserName { get; set; } = "";
    public string? PasswordHash { get; set; }
    public string? SecurityStamp { get; set; }
    public string? ConcurrencyStamp { get; set; } = Guid.NewGuid().ToString("D");
    public bool LockoutEnabled { get; set; } = true;
    public DateTimeOffset? LockoutEnd { get; set; }
    public int AccessFailedCount { get; set; }
    public bool TwoFactorEnabled { get; set; }

    /// <summary>TOTP shared secret (base32), set by Identity's ResetAuthenticatorKeyAsync.</summary>
    public string? AuthenticatorKey { get; set; }

    /// <summary>SHA-256 (hex) of each unused recovery code, ';'-separated. Plain codes are never stored.</summary>
    public string? RecoveryCodeHashes { get; set; }

    /// <summary>Set by the database default; Insert/Update do not write it.</summary>
    public DateTime CreatedAt { get; set; }

    public override string ToString() => UserName;
}
