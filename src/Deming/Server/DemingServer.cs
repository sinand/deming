using Deming.Data;
using Deming.Server.Api;
using Deming.Server.Exposure;
using Deming.Server.Panel;
using Microsoft.AspNetCore.Hosting.Server;

namespace Deming.Server;

/// <summary>Entry point for the server role ("deming serve").</summary>
public static class DemingServer
{
    public static async Task RunAsync(string[] args)
    {
        string? generatedAt = null;
        // The API key: argument or environment variable, else the user's config file (generated on first serve).
        var app = Build(args, b => generatedAt = UserConfig.ProvideApiKey(
            b.Configuration, Environment.GetEnvironmentVariable, ApiKeySettings.ConfigKey));
        if (generatedAt is not null)
        {
            Console.Error.WriteLine($"API key generated at {generatedAt}. The CLI reads it from there.");
            // First start: say how to connect an agent; serve itself never writes to an agent's directory.
            Console.Error.WriteLine("Connect your agent: 'deming install claude' (Claude Code commands), or 'deming install agents' at a repository root (AGENTS.md for other agents).");
        }
        await app.RunAsync();
    }

    /// <param name="certificate">Where the https certificate comes from; default: Deming's own certificate in the data
    /// directory, generated on first start. Tests pass their own.</param>
    public static WebApplication Build(string[] args, Action<WebApplicationBuilder>? configure = null,
        Func<ServerCertificate.Result>? certificate = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            // A single-file binary can be started from any directory; anchor content to the executable.
            ContentRootPath = AppContext.BaseDirectory,
        });

        // Defaults live in code: a single-file binary ships no appsettings.json beside it.
        builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
        // Keys are stored unencrypted in the owner's database on purpose; so the
        // "No XML encryptor configured" warning is muted. The filter covers this one category: its other
        // warnings go too, errors still show.
        builder.Logging.AddFilter("Microsoft.AspNetCore.DataProtection.KeyManagement.XmlKeyManager", LogLevel.Error);

        configure?.Invoke(builder);

        // Exposure guard: refuse before anything is built or opened (docs/scope.md § 4).
        var exposure = ExposureSettings.Read(builder.Configuration);
        ExposureGuard.EnsureSafe(exposure);

        // Nothing configured: https on loopback. Kestrel's own http default is never used.
        if (exposure.UsesDefaultListener)
            builder.Configuration["urls"] = ExposureSettings.DefaultUrl;

        // Kestrel serves https with Deming's own certificate unless one is configured. The callback runs only
        // when Kestrel is the server (the in-process test server never calls it), so nothing is generated then.
        if (exposure.Listeners.Any(l => l.IsHttps) && !CertificateConfigured(builder.Configuration))
        {
            var configuration = builder.Configuration;
            var lookup = certificate ?? (() => ServerCertificate.Provide(
                ServerCertificate.DataDirectory(configuration),
                UserConfig.DefaultPath(Environment.GetEnvironmentVariable),
                DateTimeOffset.UtcNow));
            builder.WebHost.ConfigureKestrel(kestrel =>
            {
                var found = lookup();
                if (found.Certificate is null)
                    throw new ExposureException("Deming refuses to start:" + Environment.NewLine + "  - " + found.Problem);
                if (found.Notice is not null)
                    Console.Error.WriteLine(found.Notice);
                kestrel.ConfigureHttpsDefaults(o => o.ServerCertificate = found.Certificate);
            });
        }

        builder.Services.AddDemingData(builder.Configuration);
        builder.Services.AddSingleton(IpAllowList.Parse(exposure.AllowedIpEntries));
        builder.Services.AddDemingPanelAuth(exposure);
        builder.Services.AddDemingPanel();
        builder.Services.AddDemingApi(builder.Configuration);

        var app = builder.Build();

        // Resolving the server runs the Kestrel setup above: no certificate means no start, before the database is touched.
        _ = app.Services.GetRequiredService<IServer>();

        // The database is created and brought to the current schema before anything listens.
        app.Services.GetRequiredService<DemingDb>().MigrateAsync().GetAwaiter().GetResult();

        // The IP gate is the first middleware: it also covers static files and anything added later.
        app.UseHsts(); // https only; the header is skipped for loopback hosts by default
        app.UseMiddleware<IpGateMiddleware>();
        app.UseDemingPanelCulture();
        app.UseAuthentication();
        app.UseDemingPanelEntry();
        app.UseAuthorization();
        app.UseDemingPanel(); // antiforgery of the component forms (after authorization, as the docs require)
        app.UseDemingApi(); // /api: X-Api-Key check and JSON errors (the routes are AllowAnonymous for the panel's policy)

        app.MapGet("/health", () => Results.Ok(new { status = "ok" })).AllowAnonymous();
        app.MapDemingPanelAuth();
        app.MapDemingPanel();
        app.MapDemingApi();

        return app;
    }

    /// <summary>A certificate set in Kestrel's own configuration (default or per endpoint) replaces Deming's own; none is generated then.</summary>
    private static bool CertificateConfigured(IConfiguration configuration) =>
        configuration.GetSection("Kestrel:Certificates:Default").Exists()
        || configuration.GetSection("Kestrel:Endpoints").GetChildren().Any(e => e.GetSection("Certificate").Exists());
}
