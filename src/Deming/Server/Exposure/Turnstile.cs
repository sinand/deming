using System.Text.Json;
using System.Text.RegularExpressions;

namespace Deming.Server.Exposure;

/// <summary>Turnstile settings. Keys come from configuration (user config, user secrets, environment), never from code.</summary>
public sealed record TurnstileOptions(bool Enabled, string SiteKey, string SecretKey)
{
    public const string VerifyUrl = "https://challenges.cloudflare.com/turnstile/v0/siteverify";

    // Cloudflare's public always-pass / always-fail / interaction test keys: 1x/2x/3x, zeros, two letters.
    private static readonly Regex TestKeyPattern = new(@"^[123]x0{10,}[A-Za-z]{2}$", RegexOptions.CultureInvariant);

    public static bool IsTestKey(string? key) => key is not null && TestKeyPattern.IsMatch(key.Trim());

    public bool HasKeys => SiteKey.Length > 0 && SecretKey.Length > 0;

    public bool HasRealKeys => HasKeys && !IsTestKey(SiteKey) && !IsTestKey(SecretKey);
}

public interface ITurnstileVerifier
{
    Task<bool> VerifyAsync(string? token, string? remoteIp, CancellationToken ct);
}

public sealed class CloudflareTurnstileVerifier(HttpClient http, TurnstileOptions options) : ITurnstileVerifier
{
    public async Task<bool> VerifyAsync(string? token, string? remoteIp, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token))
            return false;

        var form = new Dictionary<string, string>
        {
            ["secret"] = options.SecretKey,
            ["response"] = token,
        };
        if (!string.IsNullOrEmpty(remoteIp))
            form["remoteip"] = remoteIp;

        try
        {
            using var response = await http.PostAsync(TurnstileOptions.VerifyUrl, new FormUrlEncodedContent(form), ct);
            if (!response.IsSuccessStatusCode)
                return false;
            using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            return doc.RootElement.TryGetProperty("success", out var ok) && ok.ValueKind == JsonValueKind.True;
        }
        catch (HttpRequestException)
        {
            return false; // fail closed
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return false;
        }
    }
}
