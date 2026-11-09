using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Deming.Cli;

/// <summary>
/// The user's certificate trust store, behind an interface so tests never touch the real one (adding to it opens
/// a system prompt and changes the machine for good).
/// </summary>
public interface ITrustStore
{
    /// <summary>Where the certificate goes, for messages ("the Windows CurrentUser\Root store").</summary>
    string Description { get; }

    bool Contains(X509Certificate2 certificate);

    /// <summary>Adds the certificate as trusted. <paramref name="file"/> is the same certificate as a PEM file.
    /// Throws <see cref="TrustStoreException"/> when the store refuses or the user declines.</summary>
    void Add(X509Certificate2 certificate, string file);

    /// <summary>Certificates in the store whose subject is <paramref name="subject"/>. A subject can be shared by
    /// another program's certificate, so the caller decides which of them are Deming's.</summary>
    IReadOnlyList<X509Certificate2> FindBySubject(string subject);

    /// <summary>Removes the certificate (and any trust setting for it). Throws <see cref="TrustStoreException"/>
    /// when the store refuses or the user declines.</summary>
    void Remove(X509Certificate2 certificate);
}

public sealed class TrustStoreException(string message) : Exception(message);

public static class TrustStores
{
    /// <summary>The store <c>deming trust</c> writes to on this platform, or null where Deming cannot do it (Linux).</summary>
    public static ITrustStore? ForCurrentPlatform()
    {
        if (OperatingSystem.IsWindows())
            return new WindowsRootStore();
        if (OperatingSystem.IsMacOS())
            return new MacLoginKeychain();
        return null;
    }
}

/// <summary>Windows: CurrentUser\Root. Windows asks the user to confirm with a dialog; declining throws.</summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsRootStore : ITrustStore
{
    public string Description => @"the Windows CurrentUser\Root store";

    public bool Contains(X509Certificate2 certificate)
    {
        using var store = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadOnly);
        return store.Certificates.Find(X509FindType.FindByThumbprint, certificate.Thumbprint, validOnly: false).Count > 0;
    }

    public void Add(X509Certificate2 certificate, string file)
    {
        try
        {
            using var store = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
            store.Open(OpenFlags.ReadWrite);
            store.Add(certificate);
        }
        catch (CryptographicException ex)
        {
            throw new TrustStoreException($"Windows did not add the certificate ({ex.Message}). Was the confirmation dialog declined?");
        }
    }

    public IReadOnlyList<X509Certificate2> FindBySubject(string subject)
    {
        using var store = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadOnly);
        return [.. store.Certificates.Find(X509FindType.FindBySubjectDistinguishedName, subject, validOnly: false)];
    }

    public void Remove(X509Certificate2 certificate)
    {
        try
        {
            using var store = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
            store.Open(OpenFlags.ReadWrite);
            store.Remove(certificate);
        }
        catch (CryptographicException ex)
        {
            throw new TrustStoreException($"Windows did not remove the certificate ({ex.Message}). Was the confirmation dialog declined?");
        }
    }
}

/// <summary>macOS: the login keychain, trusted for SSL through the 'security' tool (it may ask for the login password).</summary>
internal sealed class MacLoginKeychain : ITrustStore
{
    private static string Keychain =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Keychains", "login.keychain-db");

    public string Description => "the macOS login keychain";

    /// <summary>Trusted when the system builds a chain for it, which for a self-signed certificate means a trust setting exists.</summary>
    public bool Contains(X509Certificate2 certificate)
    {
        using var chain = new X509Chain();
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.1"));
        return chain.Build(certificate);
    }

    public void Add(X509Certificate2 certificate, string file) =>
        Security(["add-trusted-cert", "-r", "trustRoot", "-p", "basic", "-p", "ssl", "-k", Keychain, file]);

    public IReadOnlyList<X509Certificate2> FindBySubject(string subject)
    {
        // 'security' matches the common name as a substring; the caller narrows down by the full subject.
        var cn = subject.Split(',')[0].Trim();
        var common = cn.StartsWith("CN=", StringComparison.OrdinalIgnoreCase) ? cn[3..] : cn;
        var pem = Security(["find-certificate", "-a", "-c", common, "-p", Keychain], allowFailure: true);
        var found = new List<X509Certificate2>();
        const string begin = "-----BEGIN CERTIFICATE-----";
        const string end = "-----END CERTIFICATE-----";
        for (var at = pem.IndexOf(begin, StringComparison.Ordinal); at >= 0; at = pem.IndexOf(begin, at + begin.Length, StringComparison.Ordinal))
        {
            var stop = pem.IndexOf(end, at, StringComparison.Ordinal);
            if (stop < 0)
                break;
            var candidate = X509Certificate2.CreateFromPem(pem.AsSpan(at, stop + end.Length - at));
            if (candidate.Subject == subject)
                found.Add(candidate);
            else
                candidate.Dispose();
        }
        return found;
    }

    public void Remove(X509Certificate2 certificate)
    {
        var file = Path.Combine(Path.GetTempPath(), "deming-old-" + Guid.NewGuid().ToString("N") + ".crt");
        File.WriteAllText(file, certificate.ExportCertificatePem(), Encoding.ASCII);
        try
        {
            Security(["remove-trusted-cert", file], allowFailure: true); // there may be no trust setting to remove
            Security(["delete-certificate", "-Z", certificate.GetCertHashString(HashAlgorithmName.SHA1), Keychain]);
        }
        finally
        {
            File.Delete(file);
        }
    }

    private static string Security(string[] arguments, bool allowFailure = false)
    {
        var start = new ProcessStartInfo("security")
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            StandardErrorEncoding = Encoding.UTF8,
            StandardOutputEncoding = Encoding.UTF8,
            UseShellExecute = false,
        };
        foreach (var arg in arguments)
            start.ArgumentList.Add(arg);

        using var process = Process.Start(start) ?? throw new TrustStoreException("Could not run 'security'.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0 && !allowFailure)
            throw new TrustStoreException($"'security {arguments[0]}' failed (exit {process.ExitCode}): {error.Trim()}");
        return process.ExitCode == 0 ? output.GetAwaiter().GetResult() : "";
    }
}
