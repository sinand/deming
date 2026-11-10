using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Nodes;
using Deming.Server.Exposure;

namespace Deming.Cli;

/// <summary>
/// <c>deming trust</c>: adds the certificate <c>deming serve</c> generated to the user's trust store,
/// so the browser opens the panel without a warning. Only the user runs it; <c>serve</c> never writes to a store
/// and the CLI does not need it (it trusts the certificate by fingerprint). Talks to no server.
/// </summary>
internal static class TrustCommand
{
    public static Task<int> Run(CliRun run)
    {
        var ctx = run.Ctx;
        var file = run.Args.Value("file") is { } given
            ? Path.GetFullPath(given, ctx.CurrentDirectory)
            : Path.Combine(ctx.DataDirectory ?? ServerCertificate.DefaultDataDirectory(), ServerCertificate.PublicFileName);
        if (!File.Exists(file))
            throw new CliException($"No certificate at {file}. Run 'deming serve' once: it generates the certificate there. "
                + "A server started with --Deming:DatabasePath keeps it beside the database; pass that file with --file <path>.");

        using var certificate = Load(file);
        var fingerprint = ServerCertificate.Fingerprint(certificate);

        // The file must be the certificate the CLI already trusts, so a stale or foreign file is never trusted by mistake.
        var configPath = UserConfig.DefaultPath(ctx.Env);
        byte[]? recorded;
        try
        {
            recorded = CertificatePin.Parse(UserConfig.Read(configPath).CertificateSha256, configPath);
        }
        catch (InvalidOperationException ex)
        {
            throw new CliException(ex.Message);
        }
        if (recorded is not null && !CryptographicOperations.FixedTimeEquals(recorded, certificate.GetCertHash(HashAlgorithmName.SHA256)))
            throw new CliException($"{file} is not the certificate 'deming serve' recorded in {configPath} ({UserConfig.CertificateKey}). "
                + "Start 'deming serve' once so the two agree, or pass the server's certificate with --file <path>.");

        var now = DateTime.UtcNow;
        if (now < certificate.NotBefore.ToUniversalTime() || now >= certificate.NotAfter.ToUniversalTime())
            throw new CliException($"{file} is not valid now (valid {certificate.NotBefore:yyyy-MM-dd} to {certificate.NotAfter:yyyy-MM-dd}). Start 'deming serve': it renews the certificate.");

        var store = (ctx.TrustStore ?? TrustStores.ForCurrentPlatform)();
        var result = new JsonObject { ["certificate"] = file, ["sha256"] = fingerprint };

        if (store is null)
        {
            result["trusted"] = false;
            ctx.Out.WriteLine(result.ToJsonString());
            ctx.Err.WriteLine(ManualSteps(file));
            return Task.FromResult(ExitCodes.Error);
        }

        result["store"] = store.Description;
        var added = false;
        if (store.Contains(certificate))
        {
            ctx.Err.WriteLine($"Already trusted in {store.Description}.");
        }
        else
        {
            ctx.Err.WriteLine($"Adding the Deming certificate to {store.Description}; the system may ask you to confirm.");
            // The Windows dialog identifies the certificate by its SHA-1 thumbprint only, in groups of eight:
            // print it the same way, so the number on screen can be compared before answering Yes.
            ctx.Err.WriteLine($"  Its SHA-1 thumbprint, the number a confirmation dialog shows: {string.Join(' ', certificate.Thumbprint.Chunk(8).Select(c => new string(c)))}");
            try
            {
                store.Add(certificate, file);
            }
            catch (TrustStoreException ex)
            {
                throw new CliException(ex.Message);
            }
            added = true;
        }

        // Only after the new certificate is in: a declined prompt must not leave the machine with neither.
        var removed = RemoveOldCertificates(store, certificate, ctx.Err);
        result["trusted"] = true;
        result["added"] = added;
        result["removed"] = new JsonArray(removed.Select(t => (JsonNode?)JsonValue.Create(t)).ToArray());
        ctx.Out.WriteLine(result.ToJsonString());
        if (added)
            ctx.Err.WriteLine("Trusted. Restart the browser if it still warns. After 'deming serve' renews the certificate (yearly), run 'deming trust' again.");
        return Task.FromResult(ExitCodes.Success);
    }

    /// <summary>
    /// Removes the certificates an earlier 'deming serve' generated: same subject as Deming's, self-signed, a leaf
    /// (not a CA) for server authentication, and not the current one. Anything else with that subject is left alone.
    /// A failure to remove one is reported but does not fail the command: the new certificate is already trusted.
    /// Returns the SHA-256 fingerprints removed.
    /// </summary>
    internal static List<string> RemoveOldCertificates(ITrustStore store, X509Certificate2 current, TextWriter err)
    {
        var removed = new List<string>();
        foreach (var old in store.FindBySubject(ServerCertificate.Subject))
            using (old)
            {
                if (old.Thumbprint == current.Thumbprint || !IsDemingCertificate(old))
                    continue;
                try
                {
                    store.Remove(old);
                    removed.Add(ServerCertificate.Fingerprint(old));
                    err.WriteLine($"Removed the previous Deming certificate (expires {old.NotAfter:yyyy-MM-dd}) from {store.Description}.");
                }
                catch (TrustStoreException ex)
                {
                    err.WriteLine($"warning: could not remove the previous Deming certificate ({ServerCertificate.Fingerprint(old)}): {ex.Message}");
                }
            }
        return removed;
    }

    /// <summary>What <see cref="ServerCertificate.Create"/> makes: its subject, self-signed, not a CA, server authentication.</summary>
    internal static bool IsDemingCertificate(X509Certificate2 certificate)
    {
        if (certificate.Subject != ServerCertificate.Subject || certificate.Issuer != certificate.Subject)
            return false;
        var basic = certificate.Extensions.OfType<X509BasicConstraintsExtension>().FirstOrDefault();
        if (basic is null || basic.CertificateAuthority)
            return false;
        var usage = certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>().FirstOrDefault();
        return usage is not null && usage.EnhancedKeyUsages.Cast<Oid>().Any(o => o.Value == "1.3.6.1.5.5.7.3.1");
    }

    private static X509Certificate2 Load(string file)
    {
        try
        {
            return X509Certificate2.CreateFromPem(File.ReadAllText(file, Encoding.ASCII));
        }
        catch (CryptographicException ex)
        {
            throw new CliException($"{file} is not a PEM certificate: {ex.Message}");
        }
    }

    /// <summary>Linux has no single trust store: every distribution and browser keeps its own.</summary>
    internal static string ManualSteps(string file) =>
        string.Join(Environment.NewLine,
            "Deming cannot add the certificate to a trust store on this system: each Linux distribution and browser keeps its own.",
            "The Deming CLI needs none of this (it trusts the certificate by its fingerprint). For the browser and other tools:",
            $"  Chrome, Chromium (NSS):  certutil -d sql:$HOME/.pki/nssdb -A -t \"P,,\" -n deming -i \"{file}\"",
            "                           (certutil: package libnss3-tools on Debian/Ubuntu, nss-tools on Fedora)",
            $"  Firefox:                 Settings > Privacy & Security > Certificates > View Certificates > Servers > Import, choose {file}",
            $"  curl, OpenSSL (Debian/Ubuntu):  sudo cp \"{file}\" /usr/local/share/ca-certificates/deming.crt && sudo update-ca-certificates",
            $"  curl, OpenSSL (Fedora):         sudo cp \"{file}\" /etc/pki/ca-trust/source/anchors/deming.crt && sudo update-ca-trust",
            "Or accept the browser's warning once.");
}
