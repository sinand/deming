using System.Security.Cryptography;
using System.Text;
using Deming.Data;
using Deming.Data.Models;
using Microsoft.AspNetCore.Identity;

namespace Deming.Identity;

/// <summary>
/// ASP.NET Core Identity user store over Deming's data layer (no EF).
/// Only storage lives here; password hashing, security stamps, lockout policy and TOTP are Identity's.
/// One owner account, no roles: a second CreateAsync fails with code <see cref="OwnerExistsCode"/>.
/// </summary>
public sealed class OwnerAccountStore(DemingDb db, IdentityErrorDescriber describer) :
    IUserPasswordStore<OwnerAccount>,
    IUserSecurityStampStore<OwnerAccount>,
    IUserLockoutStore<OwnerAccount>,
    IUserTwoFactorStore<OwnerAccount>,
    IUserAuthenticatorKeyStore<OwnerAccount>,
    IUserTwoFactorRecoveryCodeStore<OwnerAccount>
{
    public const string OwnerExistsCode = "OwnerExists";

    public void Dispose() { }

    /// <summary>True once the owner account exists (the first panel visit creates it).</summary>
    public async Task<bool> OwnerExistsAsync(CancellationToken ct = default) =>
        await db.Sql("OwnerAccount.Exists").GetScalarAsync<bool>(ct);

    public async Task<IdentityResult> CreateAsync(OwnerAccount user, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(user);
        try
        {
            await db.Sql("OwnerAccount.Insert").AddParameters(user).ExecuteAsync(ct);
            return IdentityResult.Success;
        }
        catch (ConstraintViolationException ex) when (ex.Kind is ConstraintKind.Unique or ConstraintKind.PrimaryKey)
        {
            return IdentityResult.Failed(new IdentityError
            {
                Code = OwnerExistsCode,
                Description = "The owner account already exists; Deming has exactly one.",
            });
        }
    }

    public async Task<IdentityResult> UpdateAsync(OwnerAccount user, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(user);
        var old = user.ConcurrencyStamp;
        user.ConcurrencyStamp = Guid.NewGuid().ToString("D");

        var rows = await db.Sql("OwnerAccount.Update")
            .AddParameters(user)
            .AddParameter("OldConcurrencyStamp", old)
            .ExecuteAsync(ct);

        if (rows == 1)
            return IdentityResult.Success;

        user.ConcurrencyStamp = old;
        return IdentityResult.Failed(describer.ConcurrencyFailure());
    }

    public async Task<IdentityResult> DeleteAsync(OwnerAccount user, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(user);
        var rows = await db.Sql("OwnerAccount.Delete")
            .AddParameter("Id", user.Id)
            .AddParameter("ConcurrencyStamp", user.ConcurrencyStamp)
            .ExecuteAsync(ct);
        return rows == 1 ? IdentityResult.Success : IdentityResult.Failed(describer.ConcurrencyFailure());
    }

    public Task<OwnerAccount?> FindByIdAsync(string userId, CancellationToken ct) =>
        db.Sql("OwnerAccount.GetById").AddParameter("Id", userId).GetModelAsync<OwnerAccount>(ct);

    public Task<OwnerAccount?> FindByNameAsync(string normalizedUserName, CancellationToken ct) =>
        db.Sql("OwnerAccount.GetByNormalizedUserName")
            .AddParameter("NormalizedUserName", normalizedUserName)
            .GetModelAsync<OwnerAccount>(ct);

    // --- property accessors: Identity mutates the object, UpdateAsync persists it ---

    public Task<string> GetUserIdAsync(OwnerAccount user, CancellationToken ct) => Task.FromResult(user.Id);
    public Task<string?> GetUserNameAsync(OwnerAccount user, CancellationToken ct) => Task.FromResult<string?>(user.UserName);

    public Task SetUserNameAsync(OwnerAccount user, string? userName, CancellationToken ct)
    {
        user.UserName = userName ?? "";
        return Task.CompletedTask;
    }

    public Task<string?> GetNormalizedUserNameAsync(OwnerAccount user, CancellationToken ct) =>
        Task.FromResult<string?>(user.NormalizedUserName);

    public Task SetNormalizedUserNameAsync(OwnerAccount user, string? normalizedName, CancellationToken ct)
    {
        user.NormalizedUserName = normalizedName ?? "";
        return Task.CompletedTask;
    }

    public Task SetPasswordHashAsync(OwnerAccount user, string? passwordHash, CancellationToken ct)
    {
        user.PasswordHash = passwordHash;
        return Task.CompletedTask;
    }

    public Task<string?> GetPasswordHashAsync(OwnerAccount user, CancellationToken ct) => Task.FromResult(user.PasswordHash);
    public Task<bool> HasPasswordAsync(OwnerAccount user, CancellationToken ct) => Task.FromResult(user.PasswordHash is not null);

    public Task SetSecurityStampAsync(OwnerAccount user, string stamp, CancellationToken ct)
    {
        user.SecurityStamp = stamp;
        return Task.CompletedTask;
    }

    public Task<string?> GetSecurityStampAsync(OwnerAccount user, CancellationToken ct) => Task.FromResult(user.SecurityStamp);

    public Task<DateTimeOffset?> GetLockoutEndDateAsync(OwnerAccount user, CancellationToken ct) => Task.FromResult(user.LockoutEnd);

    public Task SetLockoutEndDateAsync(OwnerAccount user, DateTimeOffset? lockoutEnd, CancellationToken ct)
    {
        user.LockoutEnd = lockoutEnd;
        return Task.CompletedTask;
    }

    public Task<int> IncrementAccessFailedCountAsync(OwnerAccount user, CancellationToken ct) =>
        Task.FromResult(++user.AccessFailedCount);

    public Task ResetAccessFailedCountAsync(OwnerAccount user, CancellationToken ct)
    {
        user.AccessFailedCount = 0;
        return Task.CompletedTask;
    }

    public Task<int> GetAccessFailedCountAsync(OwnerAccount user, CancellationToken ct) => Task.FromResult(user.AccessFailedCount);
    public Task<bool> GetLockoutEnabledAsync(OwnerAccount user, CancellationToken ct) => Task.FromResult(user.LockoutEnabled);

    public Task SetLockoutEnabledAsync(OwnerAccount user, bool enabled, CancellationToken ct)
    {
        user.LockoutEnabled = enabled;
        return Task.CompletedTask;
    }

    // --- two-factor ---

    public Task SetTwoFactorEnabledAsync(OwnerAccount user, bool enabled, CancellationToken ct)
    {
        user.TwoFactorEnabled = enabled;
        return Task.CompletedTask;
    }

    public Task<bool> GetTwoFactorEnabledAsync(OwnerAccount user, CancellationToken ct) => Task.FromResult(user.TwoFactorEnabled);

    public Task SetAuthenticatorKeyAsync(OwnerAccount user, string key, CancellationToken ct)
    {
        user.AuthenticatorKey = key;
        return Task.CompletedTask;
    }

    public Task<string?> GetAuthenticatorKeyAsync(OwnerAccount user, CancellationToken ct) => Task.FromResult(user.AuthenticatorKey);

    // Recovery codes are kept as SHA-256 hashes: they are random (50 bits), so a fast hash is
    // enough, and a copy of the database file does not hand out working codes.

    public Task ReplaceCodesAsync(OwnerAccount user, IEnumerable<string> recoveryCodes, CancellationToken ct)
    {
        var hashes = recoveryCodes.Select(HashRecoveryCode).ToList();
        user.RecoveryCodeHashes = hashes.Count == 0 ? null : string.Join(';', hashes);
        return Task.CompletedTask;
    }

    public Task<bool> RedeemCodeAsync(OwnerAccount user, string code, CancellationToken ct)
    {
        var hashes = SplitHashes(user.RecoveryCodeHashes);
        var hash = HashRecoveryCode(code);
        var index = hashes.FindIndex(h => CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(h), Encoding.ASCII.GetBytes(hash)));
        if (index < 0)
            return Task.FromResult(false);

        // Single use: UserManager persists the shortened list through UpdateAsync.
        hashes.RemoveAt(index);
        user.RecoveryCodeHashes = hashes.Count == 0 ? null : string.Join(';', hashes);
        return Task.FromResult(true);
    }

    public Task<int> CountCodesAsync(OwnerAccount user, CancellationToken ct) =>
        Task.FromResult(SplitHashes(user.RecoveryCodeHashes).Count);

    /// <summary>Case, spaces and dashes do not matter when a code is typed back.</summary>
    internal static string HashRecoveryCode(string code)
    {
        var normalized = new string(code.Where(c => c is not (' ' or '-')).ToArray()).ToUpperInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
    }

    private static List<string> SplitHashes(string? joined) =>
        string.IsNullOrEmpty(joined) ? [] : [.. joined.Split(';', StringSplitOptions.RemoveEmptyEntries)];
}
