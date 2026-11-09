using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Deming.Cli;
using Deming.Server.Exposure;

namespace Deming.Tests;

/// <summary>
/// 'deming trust' against a fake store: the real trust store is never opened (adding to it opens a system prompt
/// and changes the machine). The certificate and config live in a temporary directory.
/// </summary>
public sealed class TrustCommandTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "deming-tests", Guid.NewGuid().ToString("N"));
    private string DataDir => Path.Combine(_dir, "data");
    private string ConfigPath => Path.Combine(_dir, "config.json");
    private string CrtPath => Path.Combine(DataDir, ServerCertificate.PublicFileName);

    private sealed class FakeStore : ITrustStore
    {
        public List<(string Thumbprint, string File)> Added { get; } = [];
        public List<string> Removed { get; } = [];

        /// <summary>What the store held before the test: other certificates, possibly an earlier Deming one.</summary>
        public List<X509Certificate2> Stored { get; } = [];

        public bool Decline { get; init; }
        public bool RefuseRemoval { get; init; }
        public string Description => "the fake store";
        public bool Contains(X509Certificate2 certificate) => Stored.Any(s => s.Thumbprint == certificate.Thumbprint);

        public void Add(X509Certificate2 certificate, string file)
        {
            if (Decline)
                throw new TrustStoreException("declined");
            Added.Add((certificate.Thumbprint, file));
            Stored.Add(X509CertificateLoader.LoadCertificate(certificate.RawData));
        }

        public IReadOnlyList<X509Certificate2> FindBySubject(string subject) =>
            [.. Stored.Where(s => s.Subject == subject).Select(s => X509CertificateLoader.LoadCertificate(s.RawData))];

        public void Remove(X509Certificate2 certificate)
        {
            if (RefuseRemoval)
                throw new TrustStoreException("refused");
            Removed.Add(certificate.Thumbprint);
            Stored.RemoveAll(s => s.Thumbprint == certificate.Thumbprint);
        }
    }

    /// <summary>A certificate with Deming's subject that is not what 'deming serve' makes: it is a CA.</summary>
    private static X509Certificate2 ForeignWithDemingSubject()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(ServerCertificate.Subject, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    /// <summary>What the first 'deming serve' leaves behind: the certificate files and the fingerprint in the config.</summary>
    private X509Certificate2 Serve(DateTimeOffset? now = null) =>
        ServerCertificate.Provide(DataDir, ConfigPath, now ?? DateTimeOffset.UtcNow).Certificate!;

    private async Task<(int Code, string Out, string Err)> Trust(Func<ITrustStore?> store, params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var code = await CliApp.RunAsync(["trust", .. args], new CliContext
        {
            Out = stdout,
            Err = stderr,
            Env = name => name == UserConfig.PathVariable ? ConfigPath : null,
            CurrentDirectory = _dir,
            DataDirectory = DataDir,
            TrustStore = store,
        });
        return (code, stdout.ToString().Trim(), stderr.ToString().Trim());
    }

    [Fact]
    public async Task Adds_the_generated_certificate_once()
    {
        using var certificate = Serve();
        var store = new FakeStore();

        var (code, output, err) = await Trust(() => store);

        Assert.True(code == 0, err);
        var added = Assert.Single(store.Added);
        Assert.Equal(certificate.Thumbprint, added.Thumbprint);
        Assert.Equal(CrtPath, added.File);
        var json = JsonDocument.Parse(output).RootElement;
        Assert.True(json.GetProperty("added").GetBoolean());
        Assert.Equal(ServerCertificate.Fingerprint(certificate), json.GetProperty("sha256").GetString());

        var again = await Trust(() => store);
        Assert.Equal(0, again.Code);
        Assert.False(JsonDocument.Parse(again.Out).RootElement.GetProperty("added").GetBoolean());
        Assert.Contains("Already trusted", again.Err);
        Assert.Single(store.Added);
    }

    [Fact]
    public async Task Adds_only_the_public_certificate()
    {
        using var certificate = Serve();
        X509Certificate2? seen = null;
        var store = new CapturingStore(c => seen = c);

        Assert.Equal(0, (await Trust(() => store)).Code);
        Assert.NotNull(seen);
        Assert.False(seen.HasPrivateKey);
    }

    private sealed class CapturingStore(Action<X509Certificate2> capture) : ITrustStore
    {
        public string Description => "capturing";
        public bool Contains(X509Certificate2 certificate) => false;
        public void Add(X509Certificate2 certificate, string file) => capture(X509CertificateLoader.LoadCertificate(certificate.RawData));
        public IReadOnlyList<X509Certificate2> FindBySubject(string subject) => [];
        public void Remove(X509Certificate2 certificate) { }
    }

    [Fact]
    public async Task Renewing_removes_the_previous_deming_certificate_and_nothing_else()
    {
        using var old = ServerCertificate.Create(DateTimeOffset.UtcNow.AddDays(-340));
        using var foreign = ForeignWithDemingSubject();
        using var unrelated = new CertificateRequest("CN=Something Else", RSA.Create(2048), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        using var current = Serve();
        var store = new FakeStore();
        store.Stored.AddRange([old, foreign, unrelated]);

        var (code, output, err) = await Trust(() => store);

        Assert.True(code == 0, err);
        Assert.Equal([old.Thumbprint], store.Removed);
        Assert.Contains(store.Stored, s => s.Thumbprint == current.Thumbprint);
        Assert.Contains(store.Stored, s => s.Thumbprint == foreign.Thumbprint);
        Assert.Contains(store.Stored, s => s.Thumbprint == unrelated.Thumbprint);
        Assert.DoesNotContain(store.Stored, s => s.Thumbprint == old.Thumbprint);
        var json = JsonDocument.Parse(output).RootElement;
        Assert.Equal(ServerCertificate.Fingerprint(old), Assert.Single(json.GetProperty("removed").EnumerateArray()).GetString());
        Assert.Contains("Removed the previous Deming certificate", err);
    }

    [Fact]
    public async Task The_current_certificate_is_never_removed_and_a_second_run_removes_nothing()
    {
        using var current = Serve();
        var store = new FakeStore();

        await Trust(() => store);
        var (code, output, _) = await Trust(() => store);

        Assert.Equal(0, code);
        Assert.Empty(store.Removed);
        Assert.Empty(JsonDocument.Parse(output).RootElement.GetProperty("removed").EnumerateArray());
        Assert.Single(store.Stored);
    }

    [Fact]
    public async Task A_declined_add_leaves_the_previous_certificate_in_place()
    {
        using var old = ServerCertificate.Create(DateTimeOffset.UtcNow.AddDays(-340));
        using var current = Serve();
        var store = new FakeStore { Decline = true };
        store.Stored.Add(old);

        var (code, _, _) = await Trust(() => store);

        Assert.Equal(ExitCodes.Error, code);
        Assert.Empty(store.Removed);
        Assert.Single(store.Stored);
    }

    [Fact]
    public async Task A_refused_removal_warns_but_the_new_certificate_stays_trusted()
    {
        using var old = ServerCertificate.Create(DateTimeOffset.UtcNow.AddDays(-340));
        using var current = Serve();
        var store = new FakeStore { RefuseRemoval = true };
        store.Stored.Add(old);

        var (code, output, err) = await Trust(() => store);

        Assert.Equal(0, code);
        Assert.Contains("warning: could not remove the previous Deming certificate", err);
        Assert.Single(store.Added);
        Assert.Empty(JsonDocument.Parse(output).RootElement.GetProperty("removed").EnumerateArray());
    }

    [Fact]
    public async Task Without_a_generated_certificate_it_says_to_run_serve()
    {
        var store = new FakeStore();

        var (code, _, err) = await Trust(() => store);

        Assert.Equal(ExitCodes.Error, code);
        Assert.Contains("Run 'deming serve' once", err);
        Assert.Empty(store.Added);
    }

    [Fact]
    public async Task A_file_that_is_not_the_recorded_certificate_is_refused()
    {
        using var certificate = Serve();
        using var other = ServerCertificate.Create(DateTimeOffset.UtcNow);
        var foreign = Path.Combine(_dir, "foreign.crt");
        File.WriteAllText(foreign, other.ExportCertificatePem());
        var store = new FakeStore();

        var (code, _, err) = await Trust(() => store, "--file", foreign);

        Assert.Equal(ExitCodes.Error, code);
        Assert.Contains("is not the certificate 'deming serve' recorded", err);
        Assert.Empty(store.Added);
    }

    [Fact]
    public async Task An_expired_certificate_is_not_trusted()
    {
        // A certificate generated a year and a half ago: the files exist, the config records it, it has expired.
        using var certificate = Serve(DateTimeOffset.UtcNow.AddDays(-500));
        var store = new FakeStore();

        var (code, _, err) = await Trust(() => store);

        Assert.Equal(ExitCodes.Error, code);
        Assert.Contains("is not valid now", err);
        Assert.Empty(store.Added);
    }

    [Fact]
    public async Task A_declined_prompt_is_an_error()
    {
        using var certificate = Serve();

        var (code, output, err) = await Trust(() => new FakeStore { Decline = true });

        Assert.Equal(ExitCodes.Error, code);
        Assert.Contains("declined", err);
        Assert.Empty(output);
    }

    [Fact]
    public async Task Where_no_store_is_supported_it_prints_the_manual_steps_and_exits_1()
    {
        using var certificate = Serve();

        var (code, output, err) = await Trust(() => null);

        Assert.Equal(ExitCodes.Error, code);
        Assert.False(JsonDocument.Parse(output).RootElement.GetProperty("trusted").GetBoolean());
        Assert.Contains("certutil", err);
        Assert.Contains("update-ca-certificates", err);
        Assert.Contains(CrtPath, err);
    }

    [Fact]
    public void Trust_is_listed_in_the_usage()
    {
        var stdout = new StringWriter();
        CliApp.Run(["help"], stdout, TextWriter.Null);
        Assert.Contains("trust", stdout.ToString());
    }
}

public class CertificatePinTests
{
    [Fact]
    public void System_trusted_certificates_pass_without_a_pin() =>
        Assert.True(CertificatePin.Accept(null, null, SslPolicyErrors.None));

    [Fact]
    public void Only_the_pinned_certificate_passes_a_failed_system_check()
    {
        using var mine = ServerCertificate.Create(DateTimeOffset.UtcNow);
        using var other = ServerCertificate.Create(DateTimeOffset.UtcNow);
        var pin = mine.GetCertHash(HashAlgorithmName.SHA256);

        Assert.True(CertificatePin.Accept(pin, mine, SslPolicyErrors.RemoteCertificateChainErrors));
        Assert.False(CertificatePin.Accept(pin, other, SslPolicyErrors.RemoteCertificateChainErrors));
        Assert.False(CertificatePin.Accept(null, mine, SslPolicyErrors.RemoteCertificateChainErrors));
        Assert.False(CertificatePin.Accept(pin, null, SslPolicyErrors.RemoteCertificateNotAvailable));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    public void No_fingerprint_means_no_pin(string? value) => Assert.Null(CertificatePin.Parse(value, "config.json"));
}
