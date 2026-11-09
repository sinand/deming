using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Deming.Data;

namespace Deming.Server.Exposure;

/// <summary>
/// Deming's own HTTPS certificate. <c>deming serve</c> generates a self-signed leaf certificate
/// (not a CA) for localhost, 127.0.0.1 and ::1 on first start, keeps it in the data directory (beside the
/// database) readable by its owner only, and renews it when it has expired or is about to. The SHA-256
/// fingerprint goes to the user's config file, where the CLI reads the one certificate it trusts. Nothing here
/// touches a certificate store: browser trust is the separate <c>deming trust</c> command.
/// </summary>
public static class ServerCertificate
{
    /// <summary>The certificate with its private key (PKCS#12, no password: the file permissions protect it).</summary>
    public const string FileName = "deming-https.pfx";

    /// <summary>The public certificate only (PEM), for <c>deming trust</c> and for importing by hand.</summary>
    public const string PublicFileName = "deming-https.crt";

    public const string Subject = "CN=localhost, O=Deming";

    /// <summary>One year: within the 825 days macOS accepts for a user-trusted TLS certificate.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(365);

    /// <summary>A certificate this close to its end is replaced at the next start.</summary>
    public static readonly TimeSpan RenewBefore = TimeSpan.FromDays(30);

    /// <summary>The certificate to serve with, or the reason there is none, plus an optional note for the user.</summary>
    public sealed record Result(X509Certificate2? Certificate, string? Problem, string? Notice = null);

    /// <summary>The data directory: where the database lives, so a moved database takes its certificate along.</summary>
    public static string DataDirectory(IConfiguration configuration)
    {
        var db = configuration[DataServiceCollectionExtensions.DatabasePathKey];
        return Path.GetDirectoryName(Path.GetFullPath(string.IsNullOrWhiteSpace(db) ? DemingDb.DefaultPath() : db))!;
    }

    /// <summary>The default data directory, computed without creating it (the CLI only reads from it).</summary>
    public static string DefaultDataDirectory() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify), "Deming");

    /// <summary>SHA-256 of the DER certificate as 64 upper-case hex digits, the form kept in the config file.</summary>
    public static string Fingerprint(X509Certificate2 certificate) => certificate.GetCertHashString(HashAlgorithmName.SHA256);

    /// <summary>
    /// Loads the certificate from <paramref name="dataDirectory"/>, or generates it when it is missing, expired or
    /// due for renewal, and records its fingerprint in the config file at <paramref name="configPath"/>.
    /// </summary>
    public static Result Provide(string dataDirectory, string configPath, DateTimeOffset now)
    {
        var path = Path.Combine(dataDirectory, FileName);
        var publicPath = Path.Combine(dataDirectory, PublicFileName);
        string? notice = null;

        X509Certificate2? certificate = null;
        if (File.Exists(path))
        {
            try
            {
                certificate = X509CertificateLoader.LoadPkcs12FromFile(path, null);
            }
            catch (CryptographicException ex)
            {
                return new Result(null,
                    $"The HTTPS certificate {path} cannot be read ({ex.Message}). Delete it (and {PublicFileName} beside it) and start again: a new one is generated.");
            }
            PrivateFile.RestrictToOwner(path);

            if (!certificate.HasPrivateKey || now < certificate.NotBefore || now >= certificate.NotAfter - RenewBefore)
            {
                var reason = certificate.HasPrivateKey ? $"the old one was valid until {certificate.NotAfter:yyyy-MM-dd}" : "the old one had no private key";
                certificate.Dispose();
                certificate = null;
                notice = $"The HTTPS certificate was renewed ({reason}).";
            }
        }

        if (certificate is null)
        {
            using var created = Create(now);
            PrivateFile.WriteAllBytes(path, created.Export(X509ContentType.Pkcs12));
            PrivateFile.WriteAllBytes(publicPath, System.Text.Encoding.ASCII.GetBytes(created.ExportCertificatePem() + "\n"));
            // Re-import from PKCS#12: Kestrel on Windows needs a key it can use for TLS, not an ephemeral one.
            certificate = X509CertificateLoader.LoadPkcs12FromFile(path, null);
            var renewed = notice is not null;
            notice = (notice ?? "HTTPS certificate generated.")
                + $" {path}{Environment.NewLine}"
                + $"  The CLI trusts it by its fingerprint in {configPath}; nothing else to do for agents.{Environment.NewLine}"
                + (renewed
                    ? "  For the browser, run again: deming trust (or accept the browser's warning)."
                    : "  For the browser, run once: deming trust (or accept the browser's warning).");
        }
        else if (!File.Exists(publicPath))
            PrivateFile.WriteAllBytes(publicPath, System.Text.Encoding.ASCII.GetBytes(certificate.ExportCertificatePem() + "\n"));

        try
        {
            UserConfig.RecordCertificate(configPath, Fingerprint(certificate));
        }
        catch (InvalidOperationException ex)
        {
            certificate.Dispose();
            return new Result(null, ex.Message);
        }
        return new Result(certificate, null, notice);
    }

    /// <summary>A new self-signed leaf certificate (RSA 2048, SHA-256), valid from a day before <paramref name="now"/> for <see cref="Lifetime"/>.</summary>
    public static X509Certificate2 Create(DateTimeOffset now)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(Subject, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("localhost");
        names.AddIpAddress(IPAddress.Loopback);
        names.AddIpAddress(IPAddress.IPv6Loopback);
        request.CertificateExtensions.Add(names.Build());
        // A leaf, never a CA: trusting it cannot vouch for any other certificate.
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false)); // server authentication
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));

        return request.CreateSelfSigned(now.AddDays(-1), now.Add(Lifetime));
    }
}
