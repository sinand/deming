using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using Deming.Server.Panel.Components;
using Microsoft.AspNetCore.Localization;

namespace Deming.Server.Panel;

/// <summary>
/// The panel proper: Razor components rendered on the server (static SSR, plain form posts, no
/// circuit and no blazor.web.js), localized en + tr. Everything is behind the
/// owner login through the fallback policy of <see cref="PanelAuth"/>; the one asset, panel.css,
/// is an embedded resource so the single-file binary needs no static web asset manifest.
/// </summary>
public static class PanelHost
{
    public static readonly string[] Cultures = ["en", "tr"];
    public const string CultureCookie = "deming.culture";
    public static readonly string[] Themes = ["light", "dark"];
    public const string ThemeCookie = "deming.theme";

    public static IServiceCollection AddDemingPanel(this IServiceCollection services)
    {
        services.AddRazorComponents();
        services.AddLocalization();
        services.Configure<RequestLocalizationOptions>(o =>
        {
            o.SetDefaultCulture("en").AddSupportedCultures(Cultures).AddSupportedUICultures(Cultures);
            o.RequestCultureProviders =
            [
                new CookieRequestCultureProvider { CookieName = CultureCookie },
                new AcceptLanguageHeaderRequestCultureProvider(),
            ];
        });
        return services;
    }

    /// <summary>Before authentication: sets the request culture (login and register are localized too).</summary>
    public static void UseDemingPanelCulture(this IApplicationBuilder app) => app.UseRequestLocalization();

    /// <summary>After <c>UseAuthorization</c>: the antiforgery check of the component form posts.</summary>
    public static void UseDemingPanel(this IApplicationBuilder app) => app.UseAntiforgery();

    public static void MapDemingPanel(this WebApplication app)
    {
        app.MapRazorComponents<App>();

        // Anonymous: the login page needs the stylesheet before there is a session.
        var css = ReadCss();
        var etag = "\"" + Convert.ToHexString(SHA256.HashData(css))[..16] + "\"";
        app.MapGet("/panel.css", (HttpContext http) =>
        {
            http.Response.Headers.ETag = etag;
            http.Response.Headers.CacheControl = "no-cache"; // revalidate; the ETag makes it a 304
            return http.Request.Headers.IfNoneMatch == etag
                ? Results.StatusCode(StatusCodes.Status304NotModified)
                : Results.Bytes(css, "text/css; charset=utf-8");
        }).AllowAnonymous();

        // Language switch. A GET: it only sets a preference cookie, nothing else changes.
        app.MapGet("/culture", (HttpContext http, string c, string? r) =>
        {
            if (Cultures.Contains(c))
                http.Response.Cookies.Append(CultureCookie, CookieRequestCultureProvider.MakeCookieValue(
                    new RequestCulture(CultureInfo.GetCultureInfo(c))),
                    new CookieOptions { MaxAge = TimeSpan.FromDays(365), HttpOnly = true, SameSite = SameSiteMode.Strict, IsEssential = true });
            // Only local paths: this must not become an open redirect.
            return Results.Redirect(PanelAuth.LocalUrl(r));
        }).AllowAnonymous();

        // Theme switch, the same shape: light or dark sets the cookie, anything else (system)
        // removes it and the stylesheet follows prefers-color-scheme.
        app.MapGet("/theme", (HttpContext http, string t, string? r) =>
        {
            if (Themes.Contains(t))
                http.Response.Cookies.Append(ThemeCookie, t,
                    new CookieOptions { MaxAge = TimeSpan.FromDays(365), HttpOnly = true, SameSite = SameSiteMode.Strict, IsEssential = true });
            else
                http.Response.Cookies.Delete(ThemeCookie);
            return Results.Redirect(PanelAuth.LocalUrl(r));
        }).AllowAnonymous();
    }

    /// <summary>The theme the owner picked ("light" or "dark"), or null to follow the system.</summary>
    public static string? Theme(HttpContext http) =>
        http.Request.Cookies.TryGetValue(ThemeCookie, out var t) && Themes.Contains(t) ? t : null;

    private static byte[] ReadCss()
    {
        using var stream = typeof(PanelHost).Assembly.GetManifestResourceStream("panel/panel.css")
            ?? throw new InvalidOperationException("Embedded resource panel/panel.css is missing.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}
