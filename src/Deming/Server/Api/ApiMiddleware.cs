using System.Security.Cryptography;
using System.Text;
using Deming.Data;
using Deming.Data.Services;

namespace Deming.Server.Api;

/// <summary>
/// The API key (scope § 4, Authentication): one key, sent as <c>X-Api-Key</c>. Read from
/// <c>Deming:ApiKey</c> (user configuration or the <c>Deming__ApiKey</c> environment variable).
/// It is never written to code or to a committed file. With no key configured the API refuses everything (401).
/// </summary>
public sealed class ApiKeySettings(string? key)
{
    public const string ConfigKey = "Deming:ApiKey";
    public const string HeaderName = "X-Api-Key";
    public const string SessionHeaderName = "X-Session-Id";

    private readonly byte[]? _hash = string.IsNullOrEmpty(key) ? null : SHA256.HashData(Encoding.UTF8.GetBytes(key));

    public bool Configured => _hash is not null;

    /// <summary>Constant-time comparison of the hashes, so the key's length and content do not leak through timing.</summary>
    public bool Matches(string? candidate) =>
        _hash is not null && candidate is { Length: > 0 }
        && CryptographicOperations.FixedTimeEquals(_hash, SHA256.HashData(Encoding.UTF8.GetBytes(candidate)));
}

/// <summary>
/// Guards everything under <c>/api</c>: checks the key first, then turns any refusal into the JSON error
/// body. Routes are <c>AllowAnonymous</c> for the panel's cookie policy; this is their own door.
/// </summary>
internal sealed class ApiMiddleware(RequestDelegate next, ApiKeySettings settings, ILogger<ApiMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext http)
    {
        if (!http.Request.Path.StartsWithSegments("/api"))
        {
            await next(http);
            return;
        }

        if (!settings.Configured)
        {
            await Write(http, 401, ApiErrors.Unauthorized("api_key_not_configured",
                $"The server has no API key. Set '{ApiKeySettings.ConfigKey}' (environment variable Deming__ApiKey) and restart."));
            return;
        }
        var sent = http.Request.Headers[ApiKeySettings.HeaderName];
        if (sent.Count == 0)
        {
            await Write(http, 401, ApiErrors.Unauthorized("missing_api_key", $"Send the API key in the {ApiKeySettings.HeaderName} header."));
            return;
        }
        if (sent.Count != 1 || !settings.Matches(sent[0]))
        {
            await Write(http, 401, ApiErrors.Unauthorized("invalid_api_key", "The API key is wrong."));
            return;
        }

        try
        {
            await next(http);
        }
        catch (TaskFlowException ex)
        {
            var (status, body) = ApiErrors.From(ex);
            await Write(http, status, body);
        }
        catch (ConstraintViolationException ex)
        {
            var (status, body) = ApiErrors.From(ex);
            await Write(http, status, body);
        }
        catch (BadHttpRequestException ex)
        {
            await Write(http, 400, ApiErrors.BadRequest(ex.InnerException is { } inner ? $"{ex.Message} {inner.Message}" : ex.Message));
        }
        catch (OperationCanceledException) when (http.RequestAborted.IsCancellationRequested)
        {
            // The client went away; nobody is left to answer.
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unhandled error in {Method} {Path}", http.Request.Method, http.Request.Path);
            await Write(http, 500, new ApiError { Error = "internal_error", Message = "The server failed; see its log." });
        }
    }

    private static async Task Write(HttpContext http, int status, ApiError body)
    {
        if (http.Response.HasStarted)
            return;
        http.Response.Clear();
        http.Response.StatusCode = status;
        await http.Response.WriteAsJsonAsync(body, http.RequestAborted);
    }
}
