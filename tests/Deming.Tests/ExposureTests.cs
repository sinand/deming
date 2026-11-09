using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;
using Deming.Data;
using Deming.Server;
using Deming.Server.Exposure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Deming.Tests;

public class IpAllowListTests
{
    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("::1", true)]
    [InlineData("::ffff:127.0.0.1", true)]
    [InlineData("10.0.0.5", false)]
    [InlineData("::2", false)]
    public void Default_loopback_list(string ip, bool allowed) =>
        Assert.Equal(allowed, IpAllowList.Parse(IpAllowList.LoopbackEntries).IsAllowed(IPAddress.Parse(ip)));

    [Fact]
    public void Empty_list_blocks_everything()
    {
        var list = IpAllowList.Parse([]);
        Assert.False(list.IsAllowed(IPAddress.Loopback));
        Assert.False(list.IsAllowed(IPAddress.IPv6Loopback));
        Assert.False(list.IsAllowed(null));
    }

    [Theory]
    [InlineData("192.168.1.0/24", "192.168.1.200", true)]
    [InlineData("192.168.1.0/24", "192.168.2.1", false)]
    [InlineData("10.0.0.0/20", "10.0.15.255", true)]
    [InlineData("10.0.0.0/20", "10.0.16.0", false)]
    [InlineData("203.0.113.7", "203.0.113.7", true)]
    [InlineData("203.0.113.7", "203.0.113.8", false)]
    [InlineData("fd00::/8", "fd12::1", true)]
    [InlineData("fd00::/8", "fe80::1", false)]
    public void Cidr_and_single_addresses(string entry, string ip, bool allowed) =>
        Assert.Equal(allowed, IpAllowList.Parse([entry]).IsAllowed(IPAddress.Parse(ip)));

    [Theory]
    [InlineData("nonsense")]
    [InlineData("10.0.0.0/33")]
    [InlineData("10.0.0.0/x")]
    public void Invalid_entries_are_rejected(string entry) =>
        Assert.Throws<FormatException>(() => IpAllowList.Parse([entry]));

    [Fact]
    public void Wide_ranges_count_as_non_loopback()
    {
        Assert.False(IpAllowList.Parse(IpAllowList.LoopbackEntries).HasNonLoopbackEntry);
        Assert.True(IpAllowList.Parse(["127.0.0.1", "0.0.0.0/0"]).HasNonLoopbackEntry);
        Assert.True(IpAllowList.Parse(["10.1.2.3"]).HasNonLoopbackEntry);
    }
}

/// <summary>
/// Deming's own certificate. Every test writes the certificate and the config file under its own
/// temporary directory: the user's data and config directories are never touched.
/// </summary>
public sealed class ServerCertificateTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "deming-tests", Guid.NewGuid().ToString("N"));
    private string DataDir => Path.Combine(_dir, "data");
    private string ConfigPath => Path.Combine(_dir, "config", "config.json");
    private string PfxPath => Path.Combine(DataDir, ServerCertificate.FileName);
    private string CrtPath => Path.Combine(DataDir, ServerCertificate.PublicFileName);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private ServerCertificate.Result Provide(DateTimeOffset now) => ServerCertificate.Provide(DataDir, ConfigPath, now);

    [Fact]
    public void First_start_generates_a_loopback_leaf_certificate_and_records_its_fingerprint()
    {
        var result = Provide(Now);
        using var certificate = result.Certificate!;

        Assert.Null(result.Problem);
        Assert.True(certificate.HasPrivateKey);
        Assert.Contains("CN=localhost", certificate.Subject);
        Assert.Contains("O=Deming", certificate.Subject);
        Assert.Equal(certificate.Subject, certificate.Issuer); // self-signed
        Assert.False(((X509BasicConstraintsExtension)certificate.Extensions["2.5.29.19"]!).CertificateAuthority);
        var names = new X509SubjectAlternativeNameExtension(certificate.Extensions["2.5.29.17"]!.RawData);
        Assert.Equal(new[] { "localhost" }, names.EnumerateDnsNames());
        Assert.Equal(new[] { IPAddress.Loopback, IPAddress.IPv6Loopback }, names.EnumerateIPAddresses());
        Assert.InRange(certificate.NotAfter - certificate.NotBefore, TimeSpan.FromDays(365), TimeSpan.FromDays(367));

        Assert.Equal(ServerCertificate.Fingerprint(certificate), UserConfig.Read(ConfigPath).CertificateSha256);
        Assert.Contains("deming trust", result.Notice);

        // The public file is the same certificate without its key.
        using var published = X509Certificate2.CreateFromPem(File.ReadAllText(CrtPath));
        Assert.Equal(certificate.Thumbprint, published.Thumbprint);
        Assert.False(published.HasPrivateKey);
    }

    [Fact]
    public void Next_start_reuses_the_certificate_silently()
    {
        using var first = Provide(Now).Certificate!;

        var second = Provide(Now.AddDays(10));
        using var again = second.Certificate!;

        Assert.Equal(first.Thumbprint, again.Thumbprint);
        Assert.Null(second.Notice);
    }

    [Theory]
    [InlineData(340)] // within RenewBefore of the end
    [InlineData(400)] // expired
    public void A_certificate_near_or_past_its_end_is_renewed_and_the_new_fingerprint_recorded(int daysLater)
    {
        using var first = Provide(Now).Certificate!;

        var renewed = Provide(Now.AddDays(daysLater));
        using var second = renewed.Certificate!;

        Assert.NotEqual(first.Thumbprint, second.Thumbprint);
        Assert.Contains("renewed", renewed.Notice);
        Assert.Contains("deming trust", renewed.Notice);
        Assert.Equal(ServerCertificate.Fingerprint(second), UserConfig.Read(ConfigPath).CertificateSha256);
        using var published = X509Certificate2.CreateFromPem(File.ReadAllText(CrtPath));
        Assert.Equal(second.Thumbprint, published.Thumbprint);
    }

    [Fact]
    public void Unknown_config_fields_survive_the_fingerprint()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
        File.WriteAllText(ConfigPath, """{"apiKey":"k","url":"https://127.0.0.1:6000"}""");

        using var certificate = Provide(Now).Certificate!;

        var values = UserConfig.Read(ConfigPath);
        Assert.Equal("k", values.ApiKey);
        Assert.Equal("https://127.0.0.1:6000", values.Url);
        Assert.Equal(ServerCertificate.Fingerprint(certificate), values.CertificateSha256);
    }

    [Fact]
    public void An_unreadable_certificate_file_stops_the_start_and_is_left_alone()
    {
        Directory.CreateDirectory(DataDir);
        File.WriteAllText(PfxPath, "not a certificate");

        var result = Provide(Now);

        Assert.Null(result.Certificate);
        Assert.Contains("cannot be read", result.Problem);
        Assert.Equal("not a certificate", File.ReadAllText(PfxPath));
        Assert.False(File.Exists(ConfigPath));
    }

    [Fact]
    public void On_unix_the_key_file_is_readable_by_its_owner_only()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix file modes; on Windows the profile's ACLs protect the data directory.");
        if (OperatingSystem.IsWindows())
            return;

        using var certificate = Provide(Now).Certificate!;

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(PfxPath));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(DataDir));

        // A copied file with wider permissions is narrowed on the next start.
        File.SetUnixFileMode(PfxPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        using var again = Provide(Now).Certificate!;
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(PfxPath));
    }

    [Fact]
    public void Data_directory_is_the_database_directory()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection([new(DataServiceCollectionExtensions.DatabasePathKey, Path.Combine(_dir, "db", "x.db"))]).Build();

        Assert.Equal(Path.Combine(_dir, "db"), ServerCertificate.DataDirectory(configuration));
    }

    [Fact]
    public async Task Real_kestrel_serves_its_own_certificate_and_the_cli_trusts_it_by_fingerprint_alone()
    {
        var path = Path.Combine(DataDir, "deming.db");
        var key = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        try
        {
            await using var app = DemingServer.Build(["--urls=https://127.0.0.1:0"], b =>
            {
                b.Configuration[DataServiceCollectionExtensions.DatabasePathKey] = path;
                b.Configuration[Deming.Server.Api.ApiKeySettings.ConfigKey] = key;
            }, () => ServerCertificate.Provide(ServerCertificate.DataDirectory(new ConfigurationBuilder()
                .AddInMemoryCollection([new(DataServiceCollectionExtensions.DatabasePathKey, path)]).Build()), ConfigPath, DateTimeOffset.UtcNow));
            await app.StartAsync(Ct);
            var url = app.Urls.First();
            Assert.True(File.Exists(PfxPath));

            // No test handler: the CLI's own HTTP client, trusting the certificate by the fingerprint serve recorded.
            var env = new Dictionary<string, string>
            {
                [UserConfig.PathVariable] = ConfigPath,
                ["DEMING_URL"] = url,
                ["DEMING_API_KEY"] = key,
            };
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var code = await Deming.Cli.CliApp.RunAsync(["projects"], new Deming.Cli.CliContext
            {
                Out = stdout,
                Err = stderr,
                Env = name => env.GetValueOrDefault(name),
                CurrentDirectory = _dir,
            });
            Assert.True(code == 0, stderr.ToString());
            Assert.Equal("[]", stdout.ToString().Trim());

            // A client without the pin does not trust it.
            using var stranger = new HttpClient { BaseAddress = new Uri(url) };
            await Assert.ThrowsAsync<HttpRequestException>(() => stranger.GetAsync("/health", Ct));
        }
        finally
        {
            TempDatabase.DeleteFiles(path, new DemingDb(path).ConnectionString);
        }
    }

    [Fact]
    public async Task A_configured_kestrel_certificate_is_used_and_nothing_is_generated()
    {
        var path = TempDatabase.NewPath();
        var pfx = Path.Combine(_dir, "own.pfx");
        Directory.CreateDirectory(_dir);
        using var own = ServerCertificate.Create(DateTimeOffset.UtcNow);
        File.WriteAllBytes(pfx, own.Export(X509ContentType.Pfx, "pw"));
        try
        {
            await using var app = DemingServer.Build(["--urls=https://127.0.0.1:0"], b =>
            {
                b.Configuration[DataServiceCollectionExtensions.DatabasePathKey] = path;
                b.Configuration["Kestrel:Certificates:Default:Path"] = pfx;
                b.Configuration["Kestrel:Certificates:Default:Password"] = "pw";
            }, () => throw new InvalidOperationException("Deming's own certificate must not be asked for."));
            await app.StartAsync(Ct);

            using var client = new HttpClient(Deming.Cli.CertificatePin.Handler(own.GetCertHash(HashAlgorithmName.SHA256)))
            {
                BaseAddress = new Uri(app.Urls.First()),
            };
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health", Ct)).StatusCode);
            Assert.False(Directory.Exists(DataDir));
        }
        finally
        {
            TempDatabase.DeleteFiles(path, new DemingDb(path).ConnectionString);
        }
    }

    [Fact]
    public void Server_without_a_certificate_refuses_to_start_before_the_database_is_touched()
    {
        var path = TempDatabase.NewPath();
        try
        {
            var ex = Assert.Throws<ExposureException>(() => DemingServer.Build(
                ["--urls=https://127.0.0.1:0"],
                b => b.Configuration[DataServiceCollectionExtensions.DatabasePathKey] = path,
                () => new ServerCertificate.Result(null, "The HTTPS certificate x cannot be read.")));

            Assert.Contains("Deming refuses to start", ex.Message);
            Assert.Contains("cannot be read", ex.Message);
            Assert.False(File.Exists(path));
        }
        finally
        {
            TempDatabase.DeleteFiles(path, new DemingDb(path).ConnectionString);
        }
    }

    [Fact]
    public async Task Server_without_any_address_listens_on_the_https_default()
    {
        var path = TempDatabase.NewPath();
        try
        {
            await using var app = DemingServer.Build([],
                b => b.Configuration[DataServiceCollectionExtensions.DatabasePathKey] = path, TestCertificate.Source);

            Assert.Equal("https://127.0.0.1:5001", app.Configuration["urls"]);
        }
        finally
        {
            TempDatabase.DeleteFiles(path, new DemingDb(path).ConnectionString);
        }
    }

    [Fact]
    public void Server_refuses_plain_http_on_loopback()
    {
        var path = TempDatabase.NewPath();
        try
        {
            var ex = Assert.Throws<ExposureException>(() => DemingServer.Build(
                ["--urls=http://127.0.0.1:5000"],
                b => b.Configuration[DataServiceCollectionExtensions.DatabasePathKey] = path, TestCertificate.Source));

            Assert.Contains("http is not allowed, use https", ex.Message);
        }
        finally
        {
            TempDatabase.DeleteFiles(path, new DemingDb(path).ConnectionString);
        }
    }

    [Fact]
    public async Task Real_kestrel_serves_https_with_the_supplied_certificate_and_a_foreign_client_is_refused()
    {
        var path = TempDatabase.NewPath();
        try
        {
            await using var app = DemingServer.Build(["--urls=https://127.0.0.1:0"],
                b => b.Configuration[DataServiceCollectionExtensions.DatabasePathKey] = path, TestCertificate.Source);
            await app.StartAsync(TestContext.Current.CancellationToken);
            var url = new Uri(app.Urls.First());

            using var trusting = TestCertificate.Client(url);
            Assert.Equal(System.Net.HttpStatusCode.OK, (await trusting.GetAsync("/health", TestContext.Current.CancellationToken)).StatusCode);

            using var stranger = new HttpClient { BaseAddress = url };
            await Assert.ThrowsAsync<HttpRequestException>(() => stranger.GetAsync("/health", TestContext.Current.CancellationToken));
        }
        finally
        {
            TempDatabase.DeleteFiles(path, new DemingDb(path).ConnectionString);
        }
    }
}

public class ExposureGuardTests
{
    private const string RealSite = "0x4AAAAAAAreal-site-key";
    private const string RealSecret = "0x4AAAAAAAreal-secret-key";

    private static ExposureSettings Read(params (string Key, string Value)[] pairs) =>
        ExposureSettings.Read(new ConfigurationBuilder().AddInMemoryCollection(pairs.Select(p => KeyValuePair.Create(p.Key, (string?)p.Value))).Build());

    [Theory]
    [InlineData("1x00000000000000000000AA")]
    [InlineData("2x00000000000000000000AB")]
    [InlineData("3x00000000000000000000FF")]
    [InlineData("1x0000000000000000000000000000000AA")]
    [InlineData("2x0000000000000000000000000000000AA")]
    [InlineData("3x0000000000000000000000000000000AA")]
    public void Cloudflare_test_keys_are_recognised(string key) => Assert.True(TurnstileOptions.IsTestKey(key));

    [Fact]
    public void Real_looking_keys_are_not_test_keys()
    {
        Assert.False(TurnstileOptions.IsTestKey(RealSite));
        Assert.False(TurnstileOptions.IsTestKey(RealSecret));
    }

    [Fact]
    public void Defaults_are_loopback_only_with_turnstile_off()
    {
        var s = Read();
        Assert.Equal(IpAllowList.LoopbackEntries, s.AllowedIpEntries);
        Assert.False(s.Turnstile.Enabled);
        Assert.False(s.ListensBeyondLoopback);
        Assert.Empty(ExposureGuard.Check(s));
    }

    [Fact]
    public void Default_listener_is_https_on_loopback_5001()
    {
        var s = Read();
        var listener = Assert.Single(s.Listeners);
        Assert.Equal("https://127.0.0.1:5001", listener.Text);
        Assert.True(listener.IsHttps);
        Assert.True(listener.IsLoopback);
        Assert.True(s.UsesDefaultListener);
        Assert.False(Read(("urls", "https://127.0.0.1:6000")).UsesDefaultListener);
    }

    [Theory]
    [InlineData("urls", "http://127.0.0.1:5000")]
    [InlineData("urls", "http://localhost:5000")]
    [InlineData("urls", "https://127.0.0.1:5001;http://127.0.0.1:5000")]
    [InlineData("Kestrel:Endpoints:Web:Url", "http://127.0.0.1:5000")]
    [InlineData("http_ports", "8080")]
    public void Plain_http_is_refused_even_on_loopback(string key, string value)
    {
        var problems = ExposureGuard.Check(Read((key, value)));
        Assert.Contains(problems, p => p.Contains("http is not allowed, use https"));
    }

    [Fact]
    public void Https_on_loopback_is_allowed() =>
        Assert.Empty(ExposureGuard.Check(Read(("urls", "https://127.0.0.1:6000"))));

    [Fact]
    public void Present_but_empty_allow_list_means_nobody()
    {
        var s = Read(("Deming:AllowedIps", ""));
        Assert.Empty(s.AllowedIpEntries);
    }

    [Fact]
    public void Allow_list_accepts_delimited_string()
    {
        var s = Read(("Deming:AllowedIps", "10.0.0.1, 192.168.0.0/16"));
        Assert.Equal(["10.0.0.1", "192.168.0.0/16"], s.AllowedIpEntries);
    }

    [Theory]
    [InlineData("http://localhost:5000", false)]
    [InlineData("http://127.0.0.1:5000;http://[::1]:5000", false)]
    [InlineData("http://0.0.0.0:5000", true)]
    [InlineData("http://*:5000", true)]
    [InlineData("http://+:5000", true)]
    [InlineData("https://[::]:443", true)]
    [InlineData("http://dev.example.org:5000", true)]
    public void Listen_addresses_are_classified(string urls, bool beyond) =>
        Assert.Equal(beyond, Read(("urls", urls)).ListensBeyondLoopback);

    [Fact]
    public void Http_ports_bind_every_interface() =>
        Assert.True(Read(("http_ports", "8080")).ListensBeyondLoopback);

    [Fact]
    public void Beyond_loopback_with_nothing_set_names_every_missing_item()
    {
        var problems = ExposureGuard.Check(Read(("urls", "http://0.0.0.0:5000")));

        Assert.Contains(problems, p => p.Contains("AllowedIps"));
        Assert.Contains(problems, p => p.Contains("Turnstile"));
        Assert.Contains(problems, p => p.Contains("https"));
    }

    [Fact]
    public void Beyond_loopback_with_test_keys_is_refused()
    {
        var problems = ExposureGuard.Check(Read(
            ("urls", "https://0.0.0.0:443"),
            ("Deming:AllowedIps", "203.0.113.0/24"),
            ("Deming:Turnstile:SiteKey", "1x00000000000000000000AA"),
            ("Deming:Turnstile:SecretKey", "1x0000000000000000000000000000000AA")));

        var single = Assert.Single(problems);
        Assert.Contains("test keys", single);
    }

    [Fact]
    public void Beyond_loopback_with_loopback_only_list_is_refused()
    {
        var problems = ExposureGuard.Check(Read(
            ("urls", "https://0.0.0.0:443"),
            ("Deming:Turnstile:SiteKey", RealSite),
            ("Deming:Turnstile:SecretKey", RealSecret)));

        Assert.Contains("AllowedIps", Assert.Single(problems));
    }

    [Fact]
    public void Beyond_loopback_with_everything_in_place_starts()
    {
        var s = Read(
            ("urls", "https://0.0.0.0:443"),
            ("Deming:AllowedIps", "203.0.113.0/24"),
            ("Deming:Turnstile:SiteKey", RealSite),
            ("Deming:Turnstile:SecretKey", RealSecret));

        Assert.True(s.Turnstile.Enabled);
        Assert.Empty(ExposureGuard.Check(s));
    }

    [Fact]
    public void Turnstile_enabled_on_loopback_still_needs_keys()
    {
        var problems = ExposureGuard.Check(Read(("Deming:Turnstile:Enabled", "true")));
        Assert.Single(problems);
    }

    [Fact]
    public void Turnstile_test_keys_are_fine_on_loopback() =>
        Assert.Empty(ExposureGuard.Check(Read(
            ("Deming:Turnstile:Enabled", "true"),
            ("Deming:Turnstile:SiteKey", "1x00000000000000000000AA"),
            ("Deming:Turnstile:SecretKey", "1x0000000000000000000000000000000AA"))));

    [Fact]
    public void Build_throws_with_every_missing_item_named()
    {
        var path = TempDatabase.NewPath();
        try
        {
            var ex = Assert.Throws<ExposureException>(() => ServerTests.BuildTestServer(path, configure: b =>
                b.Configuration["urls"] = "http://0.0.0.0:5000"));

            Assert.Contains("AllowedIps", ex.Message);
            Assert.Contains("Turnstile", ex.Message);
            Assert.Contains("https", ex.Message);
            Assert.False(File.Exists(path)); // refused before the database was touched
        }
        finally
        {
            TempDatabase.DeleteFiles(path, new DemingDb(path).ConnectionString);
        }
    }
}

public partial class PanelAuthTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Test client that keeps cookies and never follows redirects (the test handler does not).</summary>
    private sealed class Browser(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        private readonly Dictionary<string, string> _cookies = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (_cookies.Count > 0)
                request.Headers.TryAddWithoutValidation("Cookie", string.Join("; ", _cookies.Select(c => $"{c.Key}={c.Value}")));
            var response = await base.SendAsync(request, ct);
            if (response.Headers.TryGetValues("Set-Cookie", out var set))
                foreach (var line in set)
                {
                    var pair = line.Split(';')[0];
                    var eq = pair.IndexOf('=');
                    var name = pair[..eq];
                    var value = pair[(eq + 1)..];
                    if (value.Length == 0 || line.Contains("expires=Thu, 01 Jan 1970", StringComparison.OrdinalIgnoreCase))
                        _cookies.Remove(name);
                    else
                        _cookies[name] = value;
                }
            return response;
        }
    }

    private sealed class FakeVerifier(bool pass) : ITurnstileVerifier
    {
        public int Calls { get; private set; }
        public Task<bool> VerifyAsync(string? token, string? remoteIp, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(pass && token == "ok");
        }
    }

    [GeneratedRegex("name=\"__RequestVerificationToken\" value=\"([^\"]+)\"")]
    private static partial Regex TokenPattern();

    [GeneratedRegex("name=\"_handler\" value=\"([^\"]+)\"")]
    private static partial Regex HandlerPattern();

    private static async Task<string> TokenFromAsync(HttpClient client, string url)
    {
        var html = await (await client.GetAsync(url, Ct)).Content.ReadAsStringAsync(Ct);
        return WebUtility.HtmlDecode(TokenPattern().Match(html).Groups[1].Value);
    }

    /// <summary>
    /// Posts a form the way a browser does: the antiforgery token and the _handler field of the
    /// named form come from the page. Without an explicit _handler the page's first form is used.
    /// </summary>
    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string url, Dictionary<string, string> fields)
    {
        var html = await (await client.GetAsync(url, Ct)).Content.ReadAsStringAsync(Ct);
        fields["__RequestVerificationToken"] = WebUtility.HtmlDecode(TokenPattern().Match(html).Groups[1].Value);
        if (!fields.ContainsKey("_handler") && HandlerPattern().Match(html) is { Success: true } handler)
            fields["_handler"] = WebUtility.HtmlDecode(handler.Groups[1].Value);
        return await client.PostAsync(url, new FormUrlEncodedContent(fields), Ct);
    }

    /// <summary>The path of a redirect, whether the server sent it relative or absolute.</summary>
    private static string? RedirectPath(HttpResponseMessage response) =>
        response.Headers.Location is not { } location ? null
        : location.IsAbsoluteUri ? location.PathAndQuery
        : location.OriginalString;

    private const string Password = "correct horse battery";

    private static Dictionary<string, string> Credentials(string password = Password) =>
        new() { ["username"] = "owner", ["password"] = password, ["confirm"] = password };

    private static async Task WithServerAsync(Func<WebApplication, HttpClient, Task> test, string? remoteIp = "127.0.0.1",
        Action<WebApplicationBuilder>? configure = null)
    {
        var path = TempDatabase.NewPath();
        try
        {
            await using var app = ServerTests.BuildTestServer(path, remoteIp, configure);
            await app.StartAsync(Ct);
            using var client = new HttpClient(new Browser(app.GetTestServer().CreateHandler())) { BaseAddress = app.GetTestClient().BaseAddress };
            await test(app, client);
        }
        finally
        {
            TempDatabase.DeleteFiles(path, new DemingDb(path).ConnectionString);
        }
    }

    [Fact]
    public Task Requests_from_outside_the_allow_list_are_blocked_on_every_path() =>
        WithServerAsync(async (_, client) =>
        {
            foreach (var url in new[] { "/health", "/", "/login", "/register", "/anything.js" })
                Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(url, Ct)).StatusCode);
        }, remoteIp: "203.0.113.9");

    [Fact]
    public Task Request_without_a_remote_address_is_blocked() =>
        WithServerAsync(async (_, client) =>
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/health", Ct)).StatusCode), remoteIp: null);

    [Fact]
    public Task Empty_allow_list_blocks_even_loopback() =>
        WithServerAsync(async (_, client) =>
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/health", Ct)).StatusCode),
            configure: b => b.Configuration["Deming:AllowedIps"] = "");

    [Fact]
    public Task Forwarded_for_header_does_not_open_the_gate() =>
        WithServerAsync(async (_, client) =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/health");
            request.Headers.Add("X-Forwarded-For", "127.0.0.1");
            Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(request, Ct)).StatusCode);
        }, remoteIp: "203.0.113.9");

    [Fact]
    public Task Configured_range_is_admitted() =>
        WithServerAsync(async (_, client) =>
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health", Ct)).StatusCode),
            remoteIp: "203.0.113.9",
            configure: b => b.Configuration["Deming:AllowedIps"] = "203.0.113.0/24");

    [Fact]
    public Task Panel_requires_login_even_on_loopback() =>
        WithServerAsync(async (_, client) =>
        {
            // Before the owner exists the first visit leads to registration.
            var first = await client.GetAsync("/", Ct);
            Assert.Equal("/register", first.Headers.Location?.ToString());

            Assert.Equal(HttpStatusCode.Redirect, (await PostAsync(client, "/register", Credentials())).StatusCode);
            await client.PostAsync("/logout", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = await TokenFromAsync(client, "/"),
            }), Ct);

            // Signed out: anonymous access is bounced to the login page.
            var anonymous = await client.GetAsync("/", Ct);
            Assert.Equal(HttpStatusCode.Redirect, anonymous.StatusCode);
            Assert.StartsWith("/login", anonymous.Headers.Location?.ToString() ?? "http://x", StringComparison.Ordinal);
        });

    [Fact]
    public Task First_visit_creates_the_owner_and_signs_in_then_registration_closes() =>
        WithServerAsync(async (app, client) =>
        {
            var created = await PostAsync(client, "/register", Credentials());
            Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/", Ct)).StatusCode);

            var again = await client.GetAsync("/register", Ct);
            Assert.Equal("/login", RedirectPath(again));

            // A second registration attempt (token taken from the signed-in page) is bounced, not honoured.
            var fields = Credentials();
            fields["username"] = "intruder";
            fields["__RequestVerificationToken"] = await TokenFromAsync(client, "/");
            var second = await client.PostAsync("/register", new FormUrlEncodedContent(fields), Ct);
            Assert.Equal("/login", RedirectPath(second));
        });

    [Fact]
    public Task Weak_password_is_refused_at_registration() =>
        WithServerAsync(async (_, client) =>
            Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(client, "/register", Credentials("short1"))).StatusCode));

    [Fact]
    public Task Login_accepts_the_right_password_and_rejects_the_wrong_one() =>
        WithServerAsync(async (_, client) =>
        {
            await PostAsync(client, "/register", Credentials());
            await client.PostAsync("/logout", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = await TokenFromAsync(client, "/"),
            }), Ct);

            var wrong = await PostAsync(client, "/login", new() { ["username"] = "owner", ["password"] = "nope" });
            Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
            Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync("/", Ct)).StatusCode);

            var right = await PostAsync(client, "/login", new() { ["username"] = "owner", ["password"] = Password });
            Assert.Equal(HttpStatusCode.Redirect, right.StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/", Ct)).StatusCode);
        });

    [Fact]
    public Task Repeated_failures_lock_the_account() =>
        WithServerAsync(async (_, client) =>
        {
            await PostAsync(client, "/register", Credentials());
            await client.PostAsync("/logout", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = await TokenFromAsync(client, "/"),
            }), Ct);

            for (var i = 0; i < 4; i++)
                Assert.Equal(HttpStatusCode.Unauthorized,
                    (await PostAsync(client, "/login", new() { ["username"] = "owner", ["password"] = "nope" })).StatusCode);

            // The fifth failure locks the account and leads to the lockout page.
            var fifth = await PostAsync(client, "/login", new() { ["username"] = "owner", ["password"] = "nope" });
            Assert.Equal("/lockout", RedirectPath(fifth));
            var page = await (await client.GetAsync("/lockout", Ct)).Content.ReadAsStringAsync(Ct);
            Assert.Contains("15 minutes", page);

            // Even the right password is refused while locked out.
            var locked = await PostAsync(client, "/login", new() { ["username"] = "owner", ["password"] = Password });
            Assert.Equal("/lockout", RedirectPath(locked));
            Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync("/", Ct)).StatusCode);
        });

    [Fact]
    public Task Post_without_antiforgery_token_is_rejected() =>
        WithServerAsync(async (_, client) =>
        {
            var response = await client.PostAsync("/register", new FormUrlEncodedContent(Credentials()), Ct);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        });

    [Fact]
    public Task Turnstile_is_off_on_loopback_by_default() =>
        WithServerAsync(async (app, client) =>
        {
            var verifier = new FakeVerifier(pass: false);
            // Default build has no Turnstile: registration works without any token.
            var created = await PostAsync(client, "/register", Credentials());
            Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);
            Assert.Equal(0, verifier.Calls);
            var html = await (await client.GetAsync("/", Ct)).Content.ReadAsStringAsync(Ct);
            Assert.DoesNotContain("cf-turnstile", html);
        });

    [Fact]
    public Task Turnstile_when_enabled_gates_login_and_registration()
    {
        var verifier = new FakeVerifier(pass: true);
        return WithServerAsync(async (_, client) =>
        {
            var page = await (await client.GetAsync("/register", Ct)).Content.ReadAsStringAsync(Ct);
            Assert.Contains("cf-turnstile", page);

            var denied = await PostAsync(client, "/register", Credentials());
            Assert.Equal(HttpStatusCode.BadRequest, denied.StatusCode);

            var fields = Credentials();
            fields["cf-turnstile-response"] = "ok";
            var accepted = await PostAsync(client, "/register", fields);
            Assert.Equal(HttpStatusCode.Redirect, accepted.StatusCode);
            Assert.Equal(2, verifier.Calls);
        }, configure: b =>
        {
            b.Configuration["Deming:Turnstile:Enabled"] = "true";
            // Cloudflare's public test keys: fine on loopback.
            b.Configuration["Deming:Turnstile:SiteKey"] = "1x00000000000000000000AA";
            b.Configuration["Deming:Turnstile:SecretKey"] = "1x0000000000000000000000000000000AA";
            b.Services.AddSingleton<ITurnstileVerifier>(verifier);
        });
    }
}
