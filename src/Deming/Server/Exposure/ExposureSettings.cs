using System.Net;

namespace Deming.Server.Exposure;

/// <summary>Everything the exposure guard decides on, read once from configuration.</summary>
/// <remarks>
/// Keys: Deming:AllowedIps (list, or one comma/semicolon separated string; unset = loopback only,
/// present but empty = nobody), Deming:Turnstile:Enabled / SiteKey / SecretKey, plus the listen
/// addresses (urls, http_ports, https_ports, Kestrel:Endpoints:*:Url).
/// </remarks>
public sealed record ExposureSettings(
    IReadOnlyList<string> AllowedIpEntries,
    TurnstileOptions Turnstile,
    IReadOnlyList<ListenAddress> Listeners)
{
    public const string AllowedIpsKey = "Deming:AllowedIps";
    public const string TurnstileSection = "Deming:Turnstile";

    /// <summary>Where "deming serve" listens when no address is configured, and where the CLI looks by default.</summary>
    public const string DefaultUrl = "https://127.0.0.1:5001";

    public bool ListensBeyondLoopback => Listeners.Any(l => !l.IsLoopback);

    /// <summary>True when no address was configured and <see cref="DefaultUrl"/> is in effect.</summary>
    public bool UsesDefaultListener => Listeners is [{ Text: DefaultUrl }];

    public static ExposureSettings Read(IConfiguration configuration)
    {
        var listeners = ReadListeners(configuration);
        var beyond = listeners.Any(l => !l.IsLoopback);

        var section = configuration.GetSection(TurnstileSection);
        var enabledText = section["Enabled"];
        // Off on loopback by default; on as soon as Deming listens beyond it.
        var enabled = bool.TryParse(enabledText, out var parsed) ? parsed : beyond;
        var turnstile = new TurnstileOptions(enabled, (section["SiteKey"] ?? "").Trim(), (section["SecretKey"] ?? "").Trim());

        return new ExposureSettings(ReadAllowedIps(configuration), turnstile, listeners);
    }

    private static IReadOnlyList<string> ReadAllowedIps(IConfiguration configuration)
    {
        var section = configuration.GetSection(AllowedIpsKey);
        var children = section.GetChildren().Select(c => c.Value).Where(v => v is not null).ToList();
        if (children.Count == 0 && section.Value is null)
            return IpAllowList.LoopbackEntries;

        var values = children.Count > 0 ? children! : [section.Value];
        return values
            .SelectMany(v => v!.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .ToList();
    }

    private static List<ListenAddress> ReadListeners(IConfiguration configuration)
    {
        var urls = new List<string>();
        void AddUrls(string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
                urls.AddRange(value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }

        AddUrls(configuration["urls"]);
        foreach (var endpoint in configuration.GetSection("Kestrel:Endpoints").GetChildren())
            AddUrls(endpoint["Url"]);

        var result = urls.Select(ListenAddress.FromUrl).ToList();

        // ASPNETCORE_HTTP_PORTS / HTTPS_PORTS bind every interface.
        foreach (var (key, scheme) in new[] { ("http_ports", "http"), ("https_ports", "https") })
            foreach (var port in (configuration[key] ?? "").Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                result.Add(new ListenAddress(scheme, "*", false, $"{scheme}://*:{port}"));

        // Nothing configured: Deming's own default, https on loopback. Kestrel's http default is never used.
        if (result.Count == 0)
            result.Add(ListenAddress.FromUrl(DefaultUrl));
        return result;
    }
}

public sealed record ListenAddress(string Scheme, string Host, bool IsLoopback, string Text)
{
    public bool IsHttps => string.Equals(Scheme, "https", StringComparison.OrdinalIgnoreCase);

    public static ListenAddress FromUrl(string url)
    {
        var schemeEnd = url.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd < 0)
            throw new FormatException($"'{url}' is not a valid listen URL.");

        var scheme = url[..schemeEnd];
        var rest = url[(schemeEnd + 3)..];
        var slash = rest.IndexOf('/');
        if (slash >= 0)
            rest = rest[..slash];

        string host;
        if (rest.StartsWith('['))
        {
            var close = rest.IndexOf(']');
            host = close < 0 ? rest : rest[1..close];
        }
        else
        {
            var colon = rest.LastIndexOf(':');
            host = colon < 0 ? rest : rest[..colon];
        }

        var loopback = host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || (IPAddress.TryParse(host, out var ip) && IPAddress.IsLoopback(ip));
        return new ListenAddress(scheme, host, loopback, url);
    }
}
