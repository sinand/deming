namespace Deming.Server.Exposure;

/// <summary>
/// First thing in the pipeline: every request (panel, API, static files) must come from an allowed
/// address. Only the socket's remote address counts; X-Forwarded-For is never read, because the
/// client writes that header.
/// </summary>
public sealed class IpGateMiddleware(RequestDelegate next, IpAllowList allowList)
{
    public Task InvokeAsync(HttpContext context)
    {
        if (allowList.IsAllowed(context.Connection.RemoteIpAddress))
            return next(context);

        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return Task.CompletedTask;
    }
}
