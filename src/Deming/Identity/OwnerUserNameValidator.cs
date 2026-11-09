using System.Text;
using Deming.Data.Models;
using Microsoft.AspNetCore.Identity;

namespace Deming.Identity;

/// <summary>
/// The user name rule of the owner account: Unicode letters and digits plus <c>- . _ @ +</c>. Identity's own
/// AllowedUserNameCharacters is a fixed list of characters and cannot say "any letter", so it is switched off
/// and this validator runs in its place. Whitespace, control characters and symbols are refused; uniqueness
/// stays with Identity's normalized name.
/// </summary>
public sealed class OwnerUserNameValidator(IdentityErrorDescriber describer) : IUserValidator<OwnerAccount>
{
    private const string Punctuation = "-._@+";

    public static bool IsAllowed(string? userName) =>
        !string.IsNullOrEmpty(userName) && userName.EnumerateRunes().All(r => Rune.IsLetterOrDigit(r) || (r.IsAscii && Punctuation.Contains((char)r.Value, StringComparison.Ordinal)));

    public Task<IdentityResult> ValidateAsync(UserManager<OwnerAccount> manager, OwnerAccount user)
    {
        ArgumentNullException.ThrowIfNull(user);
        return Task.FromResult(IsAllowed(user.UserName)
            ? IdentityResult.Success
            : IdentityResult.Failed(describer.InvalidUserName(user.UserName)));
    }
}
