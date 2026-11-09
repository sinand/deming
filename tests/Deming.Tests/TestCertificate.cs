using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Deming.Server.Exposure;

namespace Deming.Tests;

/// <summary>
/// A certificate made by the test run with the server's own generator, held in memory: the server
/// under test serves it, the test clients trust this one certificate and nothing else. Nothing is written to the
/// user's data or config directory. Shared by every test that starts a real Kestrel.
/// </summary>
internal static class TestCertificate
{
    private static readonly Lazy<X509Certificate2> Shared = new(Create);

    /// <summary>The certificate with its private key, for the server.</summary>
    public static X509Certificate2 Instance => Shared.Value;

    /// <summary>The server's certificate source for <c>DemingServer.Build</c>.</summary>
    public static Func<ServerCertificate.Result> Source { get; } = () => new ServerCertificate.Result(Instance, null);

    /// <summary>
    /// A handler for a test client: valid only for <see cref="Instance"/> (pinned by SHA-256, name must match).
    /// There is no accept-everything callback.
    /// </summary>
    public static SocketsHttpHandler Handler(bool allowAutoRedirect = true, bool useCookies = false)
    {
        var pinned = Instance.GetCertHash(HashAlgorithmName.SHA256);
        return new SocketsHttpHandler
        {
            AllowAutoRedirect = allowAutoRedirect,
            UseCookies = useCookies,
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, certificate, _, errors) =>
                {
                    // Pinned: exactly the certificate this run generated, and the name must match.
                    return certificate is not null
                        && (errors & SslPolicyErrors.RemoteCertificateNameMismatch) == 0
                        && CryptographicOperations.FixedTimeEquals(certificate.GetCertHash(HashAlgorithmName.SHA256), pinned);
                },
            },
        };
    }

    /// <summary>A client that trusts only <see cref="Instance"/>.</summary>
    public static HttpClient Client(Uri baseAddress, bool allowAutoRedirect = true, bool useCookies = false) =>
        new(Handler(allowAutoRedirect, useCookies)) { BaseAddress = baseAddress };

    private static X509Certificate2 Create()
    {
        using var created = ServerCertificate.Create(DateTimeOffset.UtcNow);
        // Re-import from PFX: Kestrel on Windows needs a key it can use for TLS, not an ephemeral one.
        var certificate = X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pfx), null);

        // Test classes run in parallel and every Kestrel they start reads this one instance. X509Certificate2
        // decodes its extensions lazily and not thread-safely: two servers starting at once could see a
        // half-decoded key usage list and refuse the certificate. Decode everything here, once, under the Lazy.
        foreach (var extension in certificate.Extensions)
        {
            if (extension is X509EnhancedKeyUsageExtension usage)
                _ = usage.EnhancedKeyUsages.Count;
            else if (extension is X509SubjectAlternativeNameExtension names)
                _ = names.EnumerateDnsNames().Count();
        }
        _ = certificate.GetCertHash(HashAlgorithmName.SHA256);
        return certificate;
    }
}
