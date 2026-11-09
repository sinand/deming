using Deming.Data;
using Deming.Data.Models;
using Deming.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Deming.Tests.Identity;

public class OwnerAccountStoreTests
{
    private const string Password = "Correct-horse-9";

    private static async Task<(TempDatabase Db, ServiceProvider Services)> CreateAsync()
    {
        var t = await TempDatabase.CreateAsync();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection([new(DataServiceCollectionExtensions.DatabasePathKey, t.Path)])
            .Build();
        var services = new ServiceCollection()
            .AddLogging()
            .AddDemingData(config)
            .BuildServiceProvider();
        return (t, services);
    }

    [Fact]
    public async Task Creates_owner_and_checks_password()
    {
        var (t, sp) = await CreateAsync();
        await using var _ = t;
        await using var __ = sp;
        using var scope = sp.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<OwnerAccount>>();
        var store = (OwnerAccountStore)scope.ServiceProvider.GetRequiredService<IUserStore<OwnerAccount>>();

        Assert.False(await store.OwnerExistsAsync(TestContext.Current.CancellationToken));
        var result = await users.CreateAsync(new OwnerAccount { UserName = "sinan" }, Password);
        Assert.True(result.Succeeded, string.Join("; ", result.Errors.Select(e => e.Description)));
        Assert.True(await store.OwnerExistsAsync(TestContext.Current.CancellationToken));

        var owner = await users.FindByNameAsync("SINAN");
        Assert.NotNull(owner);
        Assert.NotNull(owner.SecurityStamp);
        Assert.True(await users.CheckPasswordAsync(owner, Password));
        Assert.False(await users.CheckPasswordAsync(owner, "wrong"));
        Assert.Equal(owner.Id, (await users.FindByIdAsync(owner.Id))?.Id);
    }

    [Fact]
    public async Task Second_owner_is_refused()
    {
        var (t, sp) = await CreateAsync();
        await using var _ = t;
        await using var __ = sp;
        using var scope = sp.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<OwnerAccount>>();

        Assert.True((await users.CreateAsync(new OwnerAccount { UserName = "first" }, Password)).Succeeded);
        var second = await users.CreateAsync(new OwnerAccount { UserName = "second" }, Password);

        Assert.False(second.Succeeded);
        Assert.Equal(OwnerAccountStore.OwnerExistsCode, Assert.Single(second.Errors).Code);
    }

    [Fact]
    public async Task Lockout_after_repeated_failures_persists()
    {
        var (t, sp) = await CreateAsync();
        await using var _ = t;
        await using var __ = sp;
        using var scope = sp.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<OwnerAccount>>();
        await users.CreateAsync(new OwnerAccount { UserName = "owner" }, Password);
        var owner = (await users.FindByNameAsync("owner"))!;

        for (var i = 0; i < 5; i++)
            await users.AccessFailedAsync(owner);

        var reloaded = (await users.FindByIdAsync(owner.Id))!;
        Assert.True(await users.IsLockedOutAsync(reloaded));
        Assert.NotNull(reloaded.LockoutEnd);
        Assert.True(reloaded.LockoutEnd > DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task Password_change_rotates_security_stamp()
    {
        var (t, sp) = await CreateAsync();
        await using var _ = t;
        await using var __ = sp;
        using var scope = sp.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<OwnerAccount>>();
        await users.CreateAsync(new OwnerAccount { UserName = "owner" }, Password);
        var owner = (await users.FindByNameAsync("owner"))!;
        var stamp = owner.SecurityStamp;

        var result = await users.ChangePasswordAsync(owner, Password, "Another-pass-7");

        Assert.True(result.Succeeded);
        var reloaded = (await users.FindByIdAsync(owner.Id))!;
        Assert.NotEqual(stamp, reloaded.SecurityStamp);
        Assert.True(await users.CheckPasswordAsync(reloaded, "Another-pass-7"));
    }

    [Fact]
    public async Task Recovery_codes_are_stored_hashed_and_redeemed_once()
    {
        var (t, sp) = await CreateAsync();
        await using var _ = t;
        await using var __ = sp;
        using var scope = sp.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<OwnerAccount>>();
        await users.CreateAsync(new OwnerAccount { UserName = "owner" }, Password);
        var owner = (await users.FindByNameAsync("owner"))!;

        var codes = (await users.GenerateNewTwoFactorRecoveryCodesAsync(owner, 10))!.ToList();
        Assert.Equal(10, codes.Count);

        var stored = (await users.FindByIdAsync(owner.Id))!;
        Assert.Equal(10, await users.CountRecoveryCodesAsync(stored));
        foreach (var code in codes)
            Assert.DoesNotContain(code, stored.RecoveryCodeHashes!, StringComparison.OrdinalIgnoreCase);

        // Case, spaces and dashes do not matter; a code works once.
        var typed = " " + codes[0].ToLowerInvariant().Replace("-", "", StringComparison.Ordinal) + " ";
        Assert.True((await users.RedeemTwoFactorRecoveryCodeAsync(stored, typed)).Succeeded);
        var after = (await users.FindByIdAsync(owner.Id))!;
        Assert.Equal(9, await users.CountRecoveryCodesAsync(after));
        Assert.False((await users.RedeemTwoFactorRecoveryCodeAsync(after, codes[0])).Succeeded);
        Assert.False((await users.RedeemTwoFactorRecoveryCodeAsync(after, "not-a-code")).Succeeded);

        // New codes replace the old ones.
        await users.GenerateNewTwoFactorRecoveryCodesAsync(after, 10);
        Assert.False((await users.RedeemTwoFactorRecoveryCodeAsync((await users.FindByIdAsync(owner.Id))!, codes[1])).Succeeded);
    }

    [Fact]
    public async Task Two_factor_state_persists_and_the_schema_refuses_it_without_a_key()
    {
        var (t, sp) = await CreateAsync();
        await using var _ = t;
        await using var __ = sp;
        using var scope = sp.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<OwnerAccount>>();
        await users.CreateAsync(new OwnerAccount { UserName = "owner" }, Password);
        var owner = (await users.FindByNameAsync("owner"))!;

        // The database rule: two-factor on needs an authenticator key.
        await Assert.ThrowsAsync<ConstraintViolationException>(() => users.SetTwoFactorEnabledAsync(owner, true));

        var fresh = (await users.FindByIdAsync(owner.Id))!;
        Assert.True((await users.ResetAuthenticatorKeyAsync(fresh)).Succeeded);
        Assert.True((await users.SetTwoFactorEnabledAsync(fresh, true)).Succeeded);

        var reloaded = (await users.FindByIdAsync(owner.Id))!;
        Assert.True(await users.GetTwoFactorEnabledAsync(reloaded));
        Assert.False(string.IsNullOrEmpty(await users.GetAuthenticatorKeyAsync(reloaded)));
    }

    [Fact]
    public async Task Stale_copy_update_is_a_concurrency_failure()
    {
        var (t, sp) = await CreateAsync();
        await using var _ = t;
        await using var __ = sp;
        using var scope = sp.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<OwnerAccount>>();
        await users.CreateAsync(new OwnerAccount { UserName = "owner" }, Password);
        var a = (await users.FindByNameAsync("owner"))!;
        var b = (await users.FindByNameAsync("owner"))!;

        Assert.True((await users.UpdateSecurityStampAsync(a)).Succeeded);
        var stale = await users.UpdateSecurityStampAsync(b);

        Assert.False(stale.Succeeded);
        Assert.Equal(nameof(IdentityErrorDescriber.ConcurrencyFailure), Assert.Single(stale.Errors).Code);
    }
}
