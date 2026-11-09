namespace Deming.Server.Exposure;

/// <summary>Thrown at startup when the configuration would expose Deming unsafely. The message names every missing item.</summary>
public sealed class ExposureException(string message) : InvalidOperationException(message);

/// <summary>
/// Startup check (docs/scope.md § 4, Exposure guard). Deming refuses to listen beyond loopback unless
/// the allow-list admits something other than loopback, Turnstile has real keys and HTTPS is used.
/// </summary>
public static class ExposureGuard
{
    /// <summary>Returns the list of problems; empty means the configuration may start.</summary>
    public static IReadOnlyList<string> Check(ExposureSettings settings)
    {
        var problems = new List<string>();

        if (settings.Turnstile.Enabled && !settings.Turnstile.HasKeys)
            problems.Add($"Turnstile is enabled but {ExposureSettings.TurnstileSection}:SiteKey and :SecretKey are not both set.");

        // No plain http listener at all, loopback included.
        var plain = settings.Listeners.Where(l => !l.IsHttps).Select(l => l.Text).ToList();
        if (plain.Count > 0)
            problems.Add($"http is not allowed, use https: {string.Join(", ", plain)}. Deming serves https only (default {ExposureSettings.DefaultUrl}).");

        if (!settings.ListensBeyondLoopback)
            return problems;

        var beyond = string.Join(", ", settings.Listeners.Where(l => !l.IsLoopback).Select(l => l.Text));
        var prefix = $"Deming listens beyond loopback ({beyond}), but";

        var list = IpAllowList.Parse(settings.AllowedIpEntries);
        if (!list.HasNonLoopbackEntry)
            problems.Add($"{prefix} {ExposureSettings.AllowedIpsKey} has no entry other than loopback. Add the address or CIDR range that may connect.");

        if (!settings.Turnstile.Enabled)
            problems.Add($"{prefix} Turnstile is turned off. Set {ExposureSettings.TurnstileSection}:Enabled to true (or remove it) and give real keys.");
        else if (settings.Turnstile.HasKeys && !settings.Turnstile.HasRealKeys)
            problems.Add($"{prefix} the Turnstile keys are Cloudflare's public test keys, which always pass. Create a real widget and set its keys.");

        return problems;
    }

    public static void EnsureSafe(ExposureSettings settings)
    {
        var problems = Check(settings);
        if (problems.Count == 0)
            return;

        throw new ExposureException(
            "Deming refuses to start:" + Environment.NewLine +
            string.Join(Environment.NewLine, problems.Select(p => "  - " + p)));
    }
}
