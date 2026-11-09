using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Deming;

/// <summary>
/// The user's config file (docs/scope.md § 4 and § 9): <c>&lt;ApplicationData&gt;/Deming/config.json</c>
/// (Windows: %APPDATA%, Linux and macOS: ~/.config), next to no binary. The data directory stays
/// <c>&lt;LocalApplicationData&gt;/Deming</c>. <c>DEMING_CONFIG</c> overrides the file path.
/// Keys: <c>apiKey</c> (written by the first <c>deming serve</c>, read by the CLI), <c>url</c> (optional, the CLI's
/// server) and <c>certificateSha256</c> (written by <c>deming serve</c>: the SHA-256 fingerprint of the HTTPS
/// certificate it generated, the one certificate the CLI trusts beyond the system's own).
/// </summary>
public static class UserConfig
{
    public const string PathVariable = "DEMING_CONFIG";
    public const string CertificateKey = "certificateSha256";

    /// <summary>What the CLI reads from the file; every value is null when absent.</summary>
    public sealed record Values(string? ApiKey, string? Url, string? CertificateSha256);

    public static string DefaultPath(Func<string, string?> env)
    {
        var overridden = env(PathVariable);
        if (!string.IsNullOrWhiteSpace(overridden))
            return Path.GetFullPath(overridden);
        var root = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.Create);
        return Path.Combine(root, "Deming", "config.json");
    }

    public static Values Read(string path)
    {
        var obj = Load(path);
        return new Values(Text(obj, "apiKey"), Text(obj, "url"), Text(obj, CertificateKey));
    }

    /// <summary>
    /// Server side: records the fingerprint of the certificate <c>deming serve</c> uses. Writes only when the
    /// value changes; returns true when it wrote.
    /// </summary>
    public static bool RecordCertificate(string path, string sha256)
    {
        var obj = Load(path);
        if (string.Equals(Text(obj, CertificateKey), sha256, StringComparison.OrdinalIgnoreCase))
            return false;
        obj[CertificateKey] = sha256;
        Save(path, obj);
        return true;
    }

    /// <summary>
    /// Server side: when no API key is configured (argument, environment variable), take the one in the config
    /// file, or generate it and write it there. Returns the file path when a key was generated, otherwise null.
    /// </summary>
    public static string? ProvideApiKey(IConfiguration configuration, Func<string, string?> env, string key)
    {
        if (!string.IsNullOrEmpty(configuration[key]))
            return null;

        var path = DefaultPath(env);
        var obj = Load(path);
        var existing = Text(obj, "apiKey");
        if (existing is not null)
        {
            configuration[key] = existing;
            return null;
        }

        var generated = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
        obj["apiKey"] = generated;
        Save(path, obj);
        configuration[key] = generated;
        return path;
    }

    private static JsonObject Load(string path)
    {
        if (!File.Exists(path))
            return new JsonObject();
        try
        {
            return JsonNode.Parse(File.ReadAllText(path, Encoding.UTF8)) as JsonObject
                ?? throw new InvalidOperationException($"{path} must hold a JSON object.");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"{path} is not valid JSON: {ex.Message}", ex);
        }
    }

    private static string? Text(JsonObject obj, string name) =>
        obj[name] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s.Trim() : null;

    // The file holds a secret: owner-only on Unix, written atomically.
    private static void Save(string path, JsonObject obj) =>
        PrivateFile.Write(path, stream =>
        {
            using var writer = new StreamWriter(stream, new UTF8Encoding(false));
            writer.Write(obj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            writer.WriteLine();
        });
}
