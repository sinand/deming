using Deming.Data.Models;
using Deming.Identity;
using Deming.Server.Exposure;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Deming.Server.Panel;

/// <summary>
/// Panel sign-in with ASP.NET Core Identity: SignInManager, the Identity cookie
/// schemes and the account pages under Components/Account. Required on loopback too:
/// the API key is the agent's door, the panel login is the human's. The first visit creates the
/// single owner account. Everything that is not explicitly anonymous requires the signed-in owner.
/// </summary>
public static class PanelAuth
{
    public const string TurnstileField = "cf-turnstile-response";

    public static IServiceCollection AddDemingPanelAuth(this IServiceCollection services, ExposureSettings exposure)
    {
        services.AddSingleton(exposure.Turnstile);
        services.AddScoped<OwnerAccountStore>(); // stateless; pages ask it whether the owner exists
        services.AddHttpClient(nameof(CloudflareTurnstileVerifier), c => c.Timeout = TimeSpan.FromSeconds(10));
        // TryAdd: tests (and later callers) can register their own verifier first.
        services.TryAddTransient<ITurnstileVerifier>(sp => new CloudflareTurnstileVerifier(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(CloudflareTurnstileVerifier)),
            sp.GetRequiredService<TurnstileOptions>()));
        services.AddAntiforgery(o =>
        {
            o.Cookie.Name = "deming.csrf";
            o.Cookie.SameSite = SameSiteMode.Strict;
        });

        // Password policy: length over composition rules.
        services.Configure<IdentityOptions>(o =>
        {
            o.Password.RequiredLength = 10;
            o.Password.RequireNonAlphanumeric = false;
            o.Password.RequireDigit = false;
            o.Password.RequireUppercase = false;
            o.Password.RequireLowercase = false;
        });

        // The user manager and store come from AddDemingData; the panel adds sign-in and TOTP.
        new IdentityBuilder(typeof(OwnerAccount), services)
            .AddSignInManager()
            .AddTokenProvider<AuthenticatorTokenProvider<OwnerAccount>>(TokenOptions.DefaultAuthenticatorProvider);

        // A changed password or a reset authenticator (new security stamp) signs other sessions
        // out on their next request, not after the default half hour.
        services.Configure<SecurityStampValidatorOptions>(o => o.ValidationInterval = TimeSpan.Zero);

        services.AddAuthentication(IdentityConstants.ApplicationScheme).AddIdentityCookies();
        Cookie(services, IdentityConstants.ApplicationScheme, "deming.auth", o =>
        {
            o.LoginPath = "/login";
            o.LogoutPath = "/logout";
            o.AccessDeniedPath = "/login";
            o.SlidingExpiration = true;
            o.ExpireTimeSpan = TimeSpan.FromHours(12);
        });
        Cookie(services, IdentityConstants.TwoFactorUserIdScheme, "deming.2fa");
        Cookie(services, IdentityConstants.TwoFactorRememberMeScheme, "deming.2fa-remember");
        Cookie(services, IdentityConstants.ExternalScheme, "deming.external");

        services.AddAuthorizationBuilder()
            .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());

        return services;
    }

    /// <summary>
    /// Every Identity cookie: HttpOnly, SameSite=Strict and Secure always. Deming listens on HTTPS
    /// only, loopback included, so a cookie never travels over plain http.
    /// </summary>
    private static void Cookie(IServiceCollection services, string scheme, string name, Action<CookieAuthenticationOptions>? more = null) =>
        services.Configure<CookieAuthenticationOptions>(scheme, o =>
        {
            o.Cookie.Name = name;
            o.Cookie.HttpOnly = true;
            o.Cookie.SameSite = SameSiteMode.Strict;
            o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            more?.Invoke(o);
        });

    public static void MapDemingPanelAuth(this IEndpointRouteBuilder app)
    {
        // The pages (login, two-factor steps, register, lockout, account) are Razor components
        // under Components/Account. Sign-out is a plain post: it renders nothing.
        app.MapPost("/logout", async (HttpContext http, SignInManager<OwnerAccount> signIn) =>
        {
            if (!await ValidAntiforgeryAsync(http))
                return Results.StatusCode(StatusCodes.Status400BadRequest);
            await signIn.SignOutAsync();
            return Results.Redirect("/login");
        });
    }

    private static async Task<bool> ValidAntiforgeryAsync(HttpContext http)
    {
        try
        {
            await http.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(http);
            return true;
        }
        catch (AntiforgeryValidationException)
        {
            return false;
        }
    }

    /// <summary>
    /// Before authorization: the very first visit to "/" (no owner yet) goes to /register instead of
    /// the login challenge, and an anonymous visit goes to /login. Everything else is decided by the fallback policy.
    /// </summary>
    public static void UseDemingPanelEntry(this IApplicationBuilder app) =>
        app.Use(async (http, next) =>
        {
            if (HttpMethods.IsGet(http.Request.Method) && http.Request.Path == "/")
            {
                var ownerExists = await http.RequestServices.GetRequiredService<OwnerAccountStore>().OwnerExistsAsync(http.RequestAborted);
                if (!ownerExists)
                {
                    http.Response.Redirect("/register");
                    return;
                }
                if (http.User.Identity?.IsAuthenticated != true)
                {
                    http.Response.Redirect("/login"); // relative, same as before the panel existed
                    return;
                }
            }
            await next();
        });

    /// <summary>A local path to return to after sign-in; anything else (another site) becomes "/".</summary>
    public static string LocalUrl(string? url) =>
        url is { Length: > 0 } && url[0] == '/' && !url.StartsWith("//", StringComparison.Ordinal) && !url.StartsWith("/\\", StringComparison.Ordinal)
            ? url : "/";
}
