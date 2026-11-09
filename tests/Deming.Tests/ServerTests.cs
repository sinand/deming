using System.Net;
using Deming.Data;
using Deming.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.TestHost;

namespace Deming.Tests;

public class ServerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A test server on its own temporary database file.</summary>
    internal static WebApplication BuildTestServer(string databasePath, string? remoteIp = "127.0.0.1",
        Action<WebApplicationBuilder>? configure = null) =>
        DemingServer.Build([], b =>
        {
            b.WebHost.UseTestServer();
            b.Configuration[DataServiceCollectionExtensions.DatabasePathKey] = databasePath;
            // TestServer has no socket; stand in for the connection's remote address before the IP gate runs.
            b.Services.AddTransient<IStartupFilter>(_ => new RemoteIpFilter(remoteIp));
            configure?.Invoke(b);
        });

    private sealed class RemoteIpFilter(string? ip) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((ctx, nextMiddleware) =>
            {
                ctx.Connection.RemoteIpAddress = ip is null ? null : IPAddress.Parse(ip);
                return nextMiddleware(ctx);
            });
            next(app);
        };
    }

    [Fact]
    public async Task Health_endpoint_returns_ok()
    {
        var path = TempDatabase.NewPath();
        try
        {
            await using var app = BuildTestServer(path);
            await app.StartAsync(Ct);
            using var client = app.GetTestClient();

            var response = await client.GetAsync("/health", Ct);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        finally
        {
            TempDatabase.DeleteFiles(path, new DemingDb(path).ConnectionString);
        }
    }

    [Fact]
    public async Task Startup_creates_and_migrates_the_configured_database()
    {
        var path = TempDatabase.NewPath();
        try
        {
            await using var app = BuildTestServer(path);

            var db = app.Services.GetRequiredService<DemingDb>();
            Assert.Equal(Path.GetFullPath(path), db.DatabasePath);
            Assert.True(File.Exists(path));
            Assert.Equal(0, await db.MigrateAsync(Ct));
        }
        finally
        {
            TempDatabase.DeleteFiles(path, new DemingDb(path).ConnectionString);
        }
    }

    [Fact]
    public async Task Data_protection_keys_live_in_the_database()
    {
        var path = TempDatabase.NewPath();
        try
        {
            string token;
            await using (var app = BuildTestServer(path))
            {
                token = app.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("test").Protect("hello");
            }

            // A second server on the same file reads the same key ring.
            await using (var again = BuildTestServer(path))
            {
                var plain = again.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("test").Unprotect(token);
                Assert.Equal("hello", plain);
            }
        }
        finally
        {
            TempDatabase.DeleteFiles(path, new DemingDb(path).ConnectionString);
        }
    }
}
