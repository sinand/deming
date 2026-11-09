using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Deming.Cli;

/// <summary>
/// How the CLI checks the server's certificate: a certificate the system already trusts passes as
/// usual, and so does exactly one more, the certificate whose SHA-256 fingerprint <c>deming serve</c> wrote to the
/// config file. There is no accept-everything path: a server whose certificate is neither (another machine, a
/// stale fingerprint) gets ordinary system validation and fails it.
/// </summary>
internal static class CertificatePin
{
    /// <summary>The fingerprint as bytes: hex, colons and spaces allowed. Null when absent; an error when malformed.</summary>
    public static byte[]? Parse(string? fingerprint, string configPath)
    {
        if (string.IsNullOrWhiteSpace(fingerprint))
            return null;
        var hex = fingerprint.Replace(":", "").Replace(" ", "");
        try
        {
            var bytes = Convert.FromHexString(hex);
            if (bytes.Length == SHA256.HashSizeInBytes)
                return bytes;
        }
        catch (FormatException)
        {
        }
        throw new CliException(
            $"'{UserConfig.CertificateKey}' in {configPath} is not a SHA-256 fingerprint (64 hex digits). Delete the line; the next 'deming serve' writes it again.");
    }

    public static bool Accept(byte[]? pinned, X509Certificate? certificate, SslPolicyErrors errors)
    {
        if (errors == SslPolicyErrors.None)
            return true;
        return pinned is not null
            && certificate is not null
            && CryptographicOperations.FixedTimeEquals(certificate.GetCertHash(HashAlgorithmName.SHA256), pinned);
    }

    public static SocketsHttpHandler Handler(byte[]? pinned) => new()
    {
        SslOptions = new SslClientAuthenticationOptions
        {
            RemoteCertificateValidationCallback = (_, certificate, _, errors) => Accept(pinned, certificate, errors),
        },
    };
}
