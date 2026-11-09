using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Deming.Data.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Deming.Tests;

/// <summary>
/// Account pages with ASP.NET Core Identity: two-step sign-in with an authenticator
/// app (the TOTP code is computed here, RFC 6238), recovery codes, lockout and the password change.
/// </summary>
public partial class PanelAuthTests
{
    [GeneratedRegex("id=\"authenticator-key\">([^<]+)<")]
    private static partial Regex KeyPattern();

    [GeneratedRegex("<li><code>([^<]+)</code></li>")]
    private static partial Regex CodePattern();

    private static async Task LogoutAsync(HttpClient client)
    {
        var response = await client.PostAsync("/logout", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = await TokenFromAsync(client, "/account"),
        }), Ct);
        Assert.Equal("/login", RedirectPath(response));
        Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync("/", Ct)).StatusCode);
    }

    private static async Task<bool> SignedInAsync(HttpClient client) =>
        (await client.GetAsync("/projects", Ct)).StatusCode == HttpStatusCode.OK;

    private static Task<HttpResponseMessage> LoginAsync(HttpClient client, string password = Password, bool rememberMe = false)
    {
        var fields = new Dictionary<string, string> { ["username"] = "owner", ["password"] = password };
        if (rememberMe)
            fields["rememberMe"] = "true";
        return PostAsync(client, "/login", fields);
    }

    /// <summary>Turns two-step sign-in on through the pages; returns the key and the recovery codes shown once.</summary>
    private static async Task<(string Key, List<string> Codes)> EnableTwoFactorAsync(HttpClient client)
    {
        var page = WebUtility.HtmlDecode(await (await client.GetAsync("/account/2fa/enable", Ct)).Content.ReadAsStringAsync(Ct));
        var key = KeyPattern().Match(page).Groups[1].Value.Replace(" ", "", StringComparison.Ordinal).ToUpperInvariant();
        Assert.False(string.IsNullOrEmpty(key));
        Assert.Contains("otpauth://totp/Deming:owner?secret=" + key, page);
        Assert.Contains("id=\"authenticator-qr\"><svg ", page);

        var wrong = await PostAsync(client, "/account/2fa/enable", new() { ["code"] = OtherCode(key) });
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);

        var done = await PostAsync(client, "/account/2fa/enable", new() { ["code"] = Totp(key) });
        Assert.Equal(HttpStatusCode.OK, done.StatusCode);
        var codes = CodePattern().Matches(await done.Content.ReadAsStringAsync(Ct)).Select(m => m.Groups[1].Value).ToList();
        Assert.Equal(10, codes.Count);
        return (key, codes);
    }

    // ------------------------------------------------------------------ TOTP (RFC 6238, as authenticator apps do)

    private static string Totp(string base32Key, long? step = null)
    {
        var counter = step ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30;
        Span<byte> message = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(message, counter);
        var hash = HMACSHA1.HashData(Base32(base32Key), message);
        var offset = hash[^1] & 0x0F;
        var binary = BinaryPrimitives.ReadInt32BigEndian(hash.AsSpan(offset)) & 0x7FFFFFFF;
        return (binary % 1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>A code that is not valid now: the code of a step far outside Identity's window.</summary>
    private static string OtherCode(string key)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30;
        // Identity accepts two steps either side of now; stay clear of a wider window.
        var valid = Enumerable.Range(-3, 7).Select(d => Totp(key, now + d)).ToHashSet();
        for (var step = now + 100; ; step++)
            if (Totp(key, step) is var code && !valid.Contains(code))
                return code;
    }

    private static byte[] Base32(string text)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var bytes = new List<byte>();
        int buffer = 0, bits = 0;
        foreach (var c in text.TrimEnd('='))
        {
            buffer = (buffer << 5) | alphabet.IndexOf(c, StringComparison.Ordinal);
            bits += 5;
            if (bits >= 8)
            {
                bytes.Add((byte)(buffer >> (bits - 8)));
                bits -= 8;
            }
        }
        return [.. bytes];
    }

    // ------------------------------------------------------------------ tests

    [Fact]
    public Task A_turkish_user_name_registers_and_signs_in() =>
        WithServerAsync(async (_, client) =>
        {
            var fields = Credentials();
            fields["username"] = "doğanlı";
            Assert.Equal(HttpStatusCode.Redirect, (await PostAsync(client, "/register", fields)).StatusCode);
            await LogoutAsync(client);

            var login = await PostAsync(client, "/login", new() { ["username"] = "Doğanlı",["password"] = Password });
            Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
            Assert.True(await SignedInAsync(client));
        });

    [Theory]
    [InlineData("two words")]
    [InlineData("tab\there")]
    [InlineData("bell\u0007")]
    [InlineData("smile😀")]
    [InlineData("a/b")]
    public Task Spaces_control_characters_and_symbols_are_refused_in_the_user_name(string userName) =>
        WithServerAsync(async (_, client) =>
        {
            var fields = Credentials();
            fields["username"] = userName;
            var response = await PostAsync(client, "/register", fields);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains("The user name may contain only letters", await response.Content.ReadAsStringAsync(Ct));
            Assert.False(await SignedInAsync(client));
        });

    [Fact]
    public Task Identity_cookies_are_secure_http_only_and_strict() =>
        WithServerAsync(async (_, client) =>
        {
            var created = await PostAsync(client, "/register", Credentials());
            var cookie = Assert.Single(created.Headers.GetValues("Set-Cookie"), c => c.StartsWith("deming.auth=", StringComparison.Ordinal));
            Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
        });

    [Fact]
    public Task Two_step_sign_in_asks_for_the_authenticator_code() =>
        WithServerAsync(async (app, client) =>
        {
            await PostAsync(client, "/register", Credentials());
            var (key, _) = await EnableTwoFactorAsync(client);

            using (var scope = app.Services.CreateScope())
            {
                var owner = (await scope.ServiceProvider.GetRequiredService<UserManager<OwnerAccount>>().FindByNameAsync("owner"))!;
                Assert.True(owner.TwoFactorEnabled);
            }
            Assert.Contains("Two-step sign-in is on.", await (await client.GetAsync("/account", Ct)).Content.ReadAsStringAsync(Ct));
            await LogoutAsync(client);

            // The password alone is not enough: the second step follows, still signed out.
            var first = await LoginAsync(client);
            Assert.StartsWith("/login/2fa", RedirectPath(first), StringComparison.Ordinal);
            Assert.False(await SignedInAsync(client));

            var step = RedirectPath(first)!;
            var wrong = await PostAsync(client, step, new() { ["code"] = OtherCode(key) });
            Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
            Assert.False(await SignedInAsync(client));

            var right = await PostAsync(client, step, new() { ["code"] = Totp(key) });
            Assert.Equal("/", RedirectPath(right));
            Assert.True(await SignedInAsync(client));
        });

    [Fact]
    public Task The_second_step_needs_the_password_step_first() =>
        WithServerAsync(async (_, client) =>
        {
            await PostAsync(client, "/register", Credentials());
            await LogoutAsync(client);
            Assert.Equal("/login", RedirectPath(await client.GetAsync("/login/2fa", Ct)));
            Assert.Equal("/login", RedirectPath(await client.GetAsync("/login/recovery", Ct)));
        });

    [Fact]
    public Task A_remembered_browser_skips_the_code_until_it_is_forgotten() =>
        WithServerAsync(async (_, client) =>
        {
            await PostAsync(client, "/register", Credentials());
            var (key, _) = await EnableTwoFactorAsync(client);
            await LogoutAsync(client);

            var step = RedirectPath(await LoginAsync(client))!;
            var right = await PostAsync(client, step, new() { ["code"] = Totp(key), ["rememberMachine"] = "true" });
            Assert.Equal("/", RedirectPath(right));
            await LogoutAsync(client);

            // Same browser: the password is enough.
            Assert.Equal("/", RedirectPath(await LoginAsync(client)));

            var forgot = await PostAsync(client, "/account/2fa", new() { ["_handler"] = "forget" });
            Assert.Equal("/account/2fa?ok=BrowserForgotten", RedirectPath(forgot));
            await LogoutAsync(client);
            Assert.StartsWith("/login/2fa", RedirectPath(await LoginAsync(client)), StringComparison.Ordinal);
        });

    [Fact]
    public Task A_recovery_code_signs_in_once() =>
        WithServerAsync(async (app, client) =>
        {
            await PostAsync(client, "/register", Credentials());
            var (_, codes) = await EnableTwoFactorAsync(client);
            await LogoutAsync(client);

            var step = RedirectPath(await LoginAsync(client))!;
            var recovery = step.Replace("/login/2fa", "/login/recovery", StringComparison.Ordinal);
            Assert.Contains("/login/recovery", await (await client.GetAsync(step, Ct)).Content.ReadAsStringAsync(Ct));

            // Typed in lower case: the code is accepted all the same.
            var used = await PostAsync(client, recovery, new() { ["code"] = codes[0].ToLowerInvariant() });
            Assert.Equal("/", RedirectPath(used));
            Assert.True(await SignedInAsync(client));
            Assert.Contains("Recovery codes left: 9.", await (await client.GetAsync("/account/2fa", Ct)).Content.ReadAsStringAsync(Ct));
            await LogoutAsync(client);

            step = RedirectPath(await LoginAsync(client))!;
            recovery = step.Replace("/login/2fa", "/login/recovery", StringComparison.Ordinal);
            var again = await PostAsync(client, recovery, new() { ["code"] = codes[0] });
            Assert.Equal(HttpStatusCode.Unauthorized, again.StatusCode);
            Assert.False(await SignedInAsync(client));

            // New codes replace the old ones; the page shows them once.
            Assert.Equal("/", RedirectPath(await PostAsync(client, recovery, new() { ["code"] = codes[1] })));
            var fresh = await PostAsync(client, "/account/2fa", new() { ["_handler"] = "codes" });
            var newCodes = CodePattern().Matches(await fresh.Content.ReadAsStringAsync(Ct)).Select(m => m.Groups[1].Value).ToList();
            Assert.Equal(10, newCodes.Count);
            Assert.Empty(newCodes.Intersect(codes));
            await LogoutAsync(client);

            step = RedirectPath(await LoginAsync(client))!;
            recovery = step.Replace("/login/2fa", "/login/recovery", StringComparison.Ordinal);
            Assert.Equal(HttpStatusCode.Unauthorized, (await PostAsync(client, recovery, new() { ["code"] = codes[2] })).StatusCode);
            Assert.Equal("/", RedirectPath(await PostAsync(client, recovery, new() { ["code"] = newCodes[0] })));
        });

    [Fact]
    public Task Wrong_authenticator_codes_lock_the_account() =>
        WithServerAsync(async (_, client) =>
        {
            await PostAsync(client, "/register", Credentials());
            var (key, _) = await EnableTwoFactorAsync(client);
            await LogoutAsync(client);

            var step = RedirectPath(await LoginAsync(client))!;
            for (var i = 0; i < 4; i++)
                Assert.Equal(HttpStatusCode.Unauthorized, (await PostAsync(client, step, new() { ["code"] = OtherCode(key) })).StatusCode);
            Assert.Equal("/lockout", RedirectPath(await PostAsync(client, step, new() { ["code"] = OtherCode(key) })));

            // Locked: the right code no longer helps, nor does the password.
            Assert.Equal("/lockout", RedirectPath(await PostAsync(client, step, new() { ["code"] = Totp(key) })));
            Assert.False(await SignedInAsync(client));
            Assert.Equal("/lockout", RedirectPath(await LoginAsync(client)));
        });

    [Fact]
    public Task Two_step_sign_in_can_be_turned_off_and_the_key_reset() =>
        WithServerAsync(async (_, client) =>
        {
            await PostAsync(client, "/register", Credentials());
            var (key, _) = await EnableTwoFactorAsync(client);

            var off = await PostAsync(client, "/account/2fa", new() { ["_handler"] = "disable" });
            Assert.Equal("/account?ok=TwoFactorDisabled", RedirectPath(off));
            Assert.True(await SignedInAsync(client)); // the new security stamp keeps this session
            await LogoutAsync(client);
            Assert.Equal("/", RedirectPath(await LoginAsync(client)));

            var (again, _) = await EnableTwoFactorAsync(client);
            Assert.Equal(key, again); // turning it off kept the key

            var reset = await PostAsync(client, "/account/2fa", new() { ["_handler"] = "reset" });
            Assert.Equal("/account/2fa/enable?ok=AuthenticatorReset", RedirectPath(reset));
            var page = WebUtility.HtmlDecode(await (await client.GetAsync("/account/2fa/enable", Ct)).Content.ReadAsStringAsync(Ct));
            var newKey = KeyPattern().Match(page).Groups[1].Value.Replace(" ", "", StringComparison.Ordinal).ToUpperInvariant();
            Assert.NotEqual(key, newKey);
            Assert.Contains("Two-step sign-in is off.", await (await client.GetAsync("/account", Ct)).Content.ReadAsStringAsync(Ct));
        });

    [Fact]
    public Task Changing_the_password_signs_other_sessions_out() =>
        WithServerAsync(async (app, client) =>
        {
            await PostAsync(client, "/register", Credentials());

            // A second browser signed in as the same owner.
            using var other = new HttpClient(new Browser(app.GetTestServer().CreateHandler())) { BaseAddress = client.BaseAddress };
            Assert.Equal("/", RedirectPath(await LoginAsync(other)));
            Assert.True(await SignedInAsync(other));

            var mismatch = await PostAsync(client, "/account", new() { ["current"] = Password, ["password"] = "new password 123", ["confirm"] = "other" });
            Assert.Equal(HttpStatusCode.BadRequest, mismatch.StatusCode);
            var wrong = await PostAsync(client, "/account", new() { ["current"] = "not it at all", ["password"] = "new password 123", ["confirm"] = "new password 123" });
            Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
            Assert.Contains("The current password is wrong.", await wrong.Content.ReadAsStringAsync(Ct));
            var tooShort = await PostAsync(client, "/account", new() { ["current"] = Password, ["password"] = "short", ["confirm"] = "short" });
            Assert.Contains("at least 10 characters", await tooShort.Content.ReadAsStringAsync(Ct));

            var changed = await PostAsync(client, "/account", new() { ["current"] = Password, ["password"] = "new password 123", ["confirm"] = "new password 123" });
            Assert.Equal("/account?ok=PasswordChanged", RedirectPath(changed));
            Assert.True(await SignedInAsync(client));
            Assert.False(await SignedInAsync(other));

            await LogoutAsync(client);
            Assert.Equal(HttpStatusCode.Unauthorized, (await LoginAsync(client)).StatusCode);
            Assert.Equal("/", RedirectPath(await LoginAsync(client, "new password 123")));
        });

    [Fact]
    public Task A_return_address_is_followed_only_when_it_is_local() =>
        WithServerAsync(async (_, client) =>
        {
            await PostAsync(client, "/register", Credentials());
            await LogoutAsync(client);

            var challenge = await client.GetAsync("/projects", Ct);
            Assert.StartsWith("/login?ReturnUrl=", RedirectPath(challenge), StringComparison.Ordinal);
            Assert.Equal("/projects", RedirectPath(await PostAsync(client, RedirectPath(challenge)!,
                new() { ["username"] = "owner", ["password"] = Password })));
            await LogoutAsync(client);

            var offsite = "/login?ReturnUrl=" + Uri.EscapeDataString("https://evil.example/");
            Assert.Equal("/", RedirectPath(await PostAsync(client, offsite, new() { ["username"] = "owner", ["password"] = Password })));
        });

    [Fact]
    public Task The_account_pages_need_the_login() =>
        WithServerAsync(async (_, client) =>
        {
            await PostAsync(client, "/register", Credentials());
            await LogoutAsync(client);
            foreach (var url in new[] { "/account", "/account/2fa", "/account/2fa/enable" })
                Assert.StartsWith("/login", RedirectPath(await client.GetAsync(url, Ct)), StringComparison.Ordinal);
        });
}
