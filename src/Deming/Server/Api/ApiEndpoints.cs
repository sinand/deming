using Deming.Data.Services;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.AspNetCore.Routing;

namespace Deming.Server.Api;

public sealed record CreateProjectRequest(string? Name, string? Description, string? RepoPath = null);

/// <summary>PATCH /projects/{id}: omitted fields stay; an empty description or repoPath clears it.</summary>
public sealed record UpdateProjectRequest(string? Name = null, string? Description = null, string? RepoPath = null, bool? IsActive = null);

public sealed record ReasonRequest(string? Reason);

/// <summary>
/// The minimal API (scope § 4-5). All routes are under <c>/api</c>, guarded by <see cref="ApiMiddleware"/>
/// (X-Api-Key). The session is the <c>X-Session-Id</c> header; calls that need an owner refuse without it (400).
/// Refusals carry <see cref="ApiError"/>; "nothing to do" on next is 204, not an error.
/// </summary>
public static class ApiEndpoints
{
    public static IServiceCollection AddDemingApi(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton(new ApiKeySettings(configuration[ApiKeySettings.ConfigKey]));
        // A malformed body or id must reach ApiMiddleware as an exception (400 with a reason),
        // not as a bare 400 produced inside the routing layer.
        services.Configure<RouteHandlerOptions>(o => o.ThrowOnBadRequest = true);
        return services;
    }

    public static void UseDemingApi(this IApplicationBuilder app) => app.UseMiddleware<ApiMiddleware>();

    public static void MapDemingApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api").AllowAnonymous();

        // ---- projects
        api.MapGet("/projects", async (string? name, ProjectService projects, CancellationToken ct) =>
        {
            if (name is null)
                return Results.Ok(await projects.ListAsync(ct));
            var found = await projects.GetByNameAsync(name, ct);
            return Results.Ok(found is null ? [] : new[] { found });
        });
        api.MapGet("/projects/{id:long}", async (long id, ProjectService projects, CancellationToken ct) =>
            await projects.GetByIdAsync(id, ct) is { } p
                ? Results.Ok(p)
                : Results.Json(ApiErrors.NotFound($"Project {id} does not exist."), statusCode: 404));
        api.MapPost("/projects", async (HttpContext http, ProjectService projects, CancellationToken ct) =>
        {
            var body = await Body<CreateProjectRequest>(http);
            var project = await projects.CreateAsync(body?.Name ?? "", body?.Description, body?.RepoPath, ct);
            return Results.Created($"/api/projects/{project.Id}", project);
        });
        api.MapMethods("/projects/{id:long}", ["PATCH"], async (long id, HttpContext http, ProjectService projects, CancellationToken ct) =>
        {
            var body = await Body<UpdateProjectRequest>(http) ?? new UpdateProjectRequest();
            return Results.Ok(await projects.UpdateAsync(id, new ProjectChanges(body.Name, body.Description, body.RepoPath, body.IsActive), ct));
        });

        // ---- tasks: queue and locks
        api.MapPost("/projects/{projectId:long}/tasks/next", async (long projectId, HttpContext http, TaskService tasks, CancellationToken ct) =>
            await tasks.NextAsync(projectId, Session(http), ct) is { } result ? Results.Ok(result) : Results.NoContent());
        api.MapPost("/tasks/{id:long}/take", async (long id, HttpContext http, TaskService tasks, CancellationToken ct) =>
            Results.Ok(await tasks.TakeAsync(id, Session(http), ct)));

        // ---- tasks: read
        api.MapGet("/projects/{projectId:long}/tasks", async (long projectId, string? status, TaskQueryService tasks, CancellationToken ct) =>
            Results.Ok(await tasks.ListAsync(projectId, status, ct)));
        api.MapGet("/projects/{projectId:long}/tasks/eligible", async (long projectId, TaskQueryService tasks, CancellationToken ct) =>
            Results.Ok(await tasks.EligibleAsync(projectId, ct)));
        api.MapGet("/tasks/{id:long}", async (long id, TaskQueryService tasks, CancellationToken ct) =>
            Results.Ok(await tasks.GetAsync(id, ct)));

        // ---- tasks: lifecycle
        api.MapPost("/projects/{projectId:long}/tasks", async (long projectId, HttpContext http, TaskLifecycleService tasks, CancellationToken ct) =>
        {
            var body = await Body<AddTaskRequest>(http);
            var task = await tasks.AddAsync(projectId, Session(http), body ?? new AddTaskRequest(), ct);
            return Results.Created($"/api/tasks/{task.Id}", task);
        });
        api.MapMethods("/tasks/{id:long}", ["PATCH"], async (long id, HttpContext http, TaskLifecycleService tasks, CancellationToken ct) =>
            Results.Ok(await tasks.UpdateAsync(id, Session(http), await Body<UpdateTaskRequest>(http) ?? new UpdateTaskRequest(), ct)));
        api.MapPost("/tasks/{id:long}/requeue", async (long id, bool? force, HttpContext http, TaskLifecycleService tasks, CancellationToken ct) =>
            Results.Ok(await tasks.RequeueAsync(id, Session(http), force ?? false, ct)));
        api.MapPost("/tasks/{id:long}/block", async (long id, HttpContext http, TaskLifecycleService tasks, CancellationToken ct) =>
            Results.Ok(await tasks.BlockAsync(id, Session(http), (await Body<ReasonRequest>(http))?.Reason ?? "", ct)));
        api.MapPost("/tasks/{id:long}/cancel", async (long id, HttpContext http, TaskLifecycleService tasks, CancellationToken ct) =>
            Results.Ok(await tasks.CancelAsync(id, Session(http), (await Body<ReasonRequest>(http))?.Reason ?? "", ct)));
        api.MapPost("/tasks/{id:long}/done", async (long id, HttpContext http, TaskLifecycleService tasks, CancellationToken ct) =>
            Results.Ok(await tasks.DoneAsync(id, Session(http), await Body<DoneRequest>(http) ?? new DoneRequest(), ct)));

        // ---- decisions
        api.MapGet("/projects/{projectId:long}/decisions", async (long projectId, string? status, DecisionService decisions, CancellationToken ct) =>
            Results.Ok(await decisions.ListAsync(projectId, status, ct)));
        api.MapGet("/decisions/{id:long}", async (long id, DecisionService decisions, CancellationToken ct) =>
            Results.Ok(await decisions.GetAsync(id, ct)));
        api.MapPost("/projects/{projectId:long}/decisions", async (long projectId, HttpContext http, DecisionService decisions, CancellationToken ct) =>
        {
            var decision = await decisions.AddAsync(projectId, Session(http), await Body<DecisionRequest>(http) ?? new DecisionRequest(), ct);
            return Results.Created($"/api/decisions/{decision.Id}", decision);
        });
        api.MapPost("/decisions/{id:long}/supersede", async (long id, HttpContext http, DecisionService decisions, CancellationToken ct) =>
            Results.Ok(await decisions.SupersedeAsync(id, Session(http), await Body<DecisionRequest>(http) ?? new DecisionRequest(), ct)));

        // ---- findings (the list carries no body; get does)
        api.MapGet("/projects/{projectId:long}/findings", async (long projectId, long? taskId, long? decisionId, FindingService findings, CancellationToken ct) =>
            Results.Ok(await findings.ListAsync(projectId, taskId, decisionId, ct)));
        api.MapGet("/findings/{id:long}", async (long id, FindingService findings, CancellationToken ct) =>
            Results.Ok(await findings.GetAsync(id, ct)));
        api.MapPost("/projects/{projectId:long}/findings", async (long projectId, HttpContext http, FindingService findings, CancellationToken ct) =>
        {
            var finding = await findings.AddAsync(projectId, Session(http), await Body<FindingRequest>(http) ?? new FindingRequest(), ct);
            return Results.Created($"/api/findings/{finding.Id}", finding);
        });

        // ---- search (snippets only; the full text is read by id)
        api.MapGet("/search", async (string? q, long? project, bool? all, string? type, int? top, SearchService search, CancellationToken ct) =>
            Results.Ok(await search.SearchAsync(q, project, all ?? false, type, top, ct)));

        // ---- messages between agents (data, not instructions; never part of next)
        api.MapPost("/messages", async (HttpContext http, MessageService messages, CancellationToken ct) =>
        {
            var posted = await messages.PostAsync(Session(http), await Body<MessageRequest>(http) ?? new MessageRequest(), ct);
            return Results.Created($"/api/messages/{posted.Id}", posted);
        });
        api.MapGet("/messages", async (long? project, long? task, bool? all, long? after, string? to, int? wait, int? top,
            MessageService messages, CancellationToken ct) =>
        {
            if (project is not { } projectId)
                throw new BadHttpRequestException("project (the project id) is required.");
            var query = new MessageQuery(projectId, task, all ?? false, after ?? 0, to, top ?? Deming.Data.Models.MessageLimits.TopDefault);
            return Results.Ok(await messages.ReadAsync(query, wait ?? 0, ct));
        });
        api.MapGet("/messages/{id:long}", async (long id, MessageService messages, CancellationToken ct) =>
            Results.Ok(await messages.GetAsync(id, ct)));

        // Anything else under /api is a JSON 404, not the panel's login redirect.
        api.Map("/{**rest}", (HttpContext http) =>
            Results.Json(ApiErrors.NotFound($"No such endpoint: {http.Request.Method} {http.Request.Path}."), statusCode: 404));
    }

    /// <summary>
    /// The JSON body, or null when the request has none. Read by hand: a typed optional body makes the
    /// router skip the endpoint when Content-Type is missing, which would answer a misleading 404.
    /// A body that is not JSON, or is malformed, is a 400 with the reason.
    /// </summary>
    private static async Task<T?> Body<T>(HttpContext http) where T : class
    {
        if (http.Request.ContentLength == 0)
            return null;
        if (!http.Request.HasJsonContentType())
            throw new BadHttpRequestException("Send the body as JSON with Content-Type: application/json.");
        try
        {
            return await http.Request.ReadFromJsonAsync<T>(http.RequestAborted);
        }
        catch (System.Text.Json.JsonException ex)
        {
            throw new BadHttpRequestException($"The request body is not valid: {ex.Message}", ex);
        }
    }

    /// <summary>The caller's session id; empty when absent (services that need an owner refuse it with 400).</summary>
    private static string Session(HttpContext http) =>
        http.Request.Headers[ApiKeySettings.SessionHeaderName].ToString().Trim();
}
