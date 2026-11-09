using Deming.Data.Models;

namespace Deming.Data.Services;

/// <summary>Input of <see cref="MessageService.PostAsync"/>.</summary>
public sealed class MessageRequest
{
    /// <summary>Project id (required).</summary>
    public long Project { get; init; }

    /// <summary>The task whose channel this is; null = the project channel.</summary>
    public long? TaskId { get; init; }

    /// <summary>The writing agent's label (required; lowercased).</summary>
    public string Sender { get; init; } = "";

    /// <summary>The addressed agent's label; null or blank = everyone.</summary>
    public string? To { get; init; }

    /// <summary>The message this one answers; it must be in the same channel.</summary>
    public long? ReplyToId { get; init; }

    public string Body { get; init; } = "";
}

/// <summary>A read of a channel: which channel, from which cursor, for whom, how many.</summary>
public sealed record MessageQuery(long ProjectId, long? TaskId = null, bool All = false, long After = 0, string? To = null, int Top = MessageLimits.TopDefault);

/// <summary>
/// The message channel between agents: write, read after a cursor (optionally waiting for the next
/// message), get one. Field rules (label shape, body cap, same-project task, read-only finished
/// channel, same-channel reply, no rewrite) live in the schema and surface as
/// <see cref="ConstraintViolationException"/>. A message hands over no lock and grants no right.
/// </summary>
public sealed class MessageService(DemingDb db, TimeProvider time)
{
    /// <summary>How often a waiting read looks at the database when no write of this process woke it
    /// (another process may write to the same file).</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    /// <summary>Completed (and replaced) after every committed write: wakes waiting reads at once.</summary>
    private TaskCompletionSource _written = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task<MessagePosted> PostAsync(string? sessionId, MessageRequest request, CancellationToken ct = default)
    {
        var posted = await db.InTransactionAsync(async tx =>
        {
            await RequireProjectAsync(tx, request.Project, ct);
            return await tx.Sql("Message.Insert")
                .AddParameter("ProjectId", request.Project).AddParameter("TaskId", request.TaskId)
                .AddParameter("Sender", request.Sender ?? "")
                .AddParameter("SessionId", string.IsNullOrWhiteSpace(sessionId) ? null : sessionId)
                .AddParameter("Recipient", request.To).AddParameter("ReplyToId", request.ReplyToId)
                .AddParameter("Body", request.Body ?? "")
                .GetModelAsync<MessagePosted>(ct)
                ?? throw new InvalidOperationException("Message.Insert returned no row.");
        }, ct);

        // After the commit, so a woken reader sees the row.
        Interlocked.Exchange(ref _written, new(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
        return posted;
    }

    /// <summary>One message with its body.</summary>
    public async Task<Message> GetAsync(long id, CancellationToken ct = default) =>
        await db.Sql("Message.GetById").AddParameter("Id", id).GetModelAsync<Message>(ct)
        ?? throw new TaskFlowException(TaskFlowError.NotFound, $"Message {id} does not exist.");

    /// <summary>
    /// Messages after the cursor. When there are none, waits up to <paramref name="waitSeconds"/>
    /// (0 to <see cref="MessageLimits.WaitMaxSeconds"/>) for one, then answers an empty page.
    /// </summary>
    public async Task<MessagePage> ReadAsync(MessageQuery query, int waitSeconds = 0, CancellationToken ct = default)
    {
        if (waitSeconds is < 0 or > MessageLimits.WaitMaxSeconds)
            throw new TaskFlowException(TaskFlowError.InvalidInput,
                $"wait must be 0 to {MessageLimits.WaitMaxSeconds} seconds; it is not shortened, ask again to wait longer.");
        if (query.Top is < 1 or > MessageLimits.TopMax)
            throw new TaskFlowException(TaskFlowError.InvalidInput, $"top must be 1 to {MessageLimits.TopMax}.");
        if (query.After < 0)
            throw new TaskFlowException(TaskFlowError.InvalidInput, "after must not be negative.");
        if (query.All && query.TaskId is not null)
            throw new TaskFlowException(TaskFlowError.InvalidInput, "Give a task or all=true, not both.");

        var deadline = time.GetUtcNow() + TimeSpan.FromSeconds(waitSeconds);
        var first = true;
        while (true)
        {
            // Taken before the read: a write committed after it completes this task, so it is never missed.
            var written = Volatile.Read(ref _written).Task;
            var page = await ReadOnceAsync(query, checkChannel: first, ct);
            first = false;

            var left = deadline - time.GetUtcNow();
            if (page.Messages.Count > 0 || left <= TimeSpan.Zero)
                return page;

            await Task.WhenAny(written, Task.Delay(left < PollInterval ? left : PollInterval, time, ct));
            ct.ThrowIfCancellationRequested();
        }
    }

    private async Task<MessagePage> ReadOnceAsync(MessageQuery query, bool checkChannel, CancellationToken ct)
    {
        if (checkChannel)
        {
            if (await db.Sql("Project.GetById").AddParameter("Id", query.ProjectId).GetModelAsync<Project>(ct) is null)
                throw new TaskFlowException(TaskFlowError.NotFound, $"Project {query.ProjectId} does not exist.");
            if (query.TaskId is { } taskId)
            {
                var task = await db.Sql("Task.GetById").AddParameter("Id", taskId).GetModelAsync<TaskItem>(ct);
                if (task is null || task.ProjectId != query.ProjectId)
                    throw new TaskFlowException(TaskFlowError.NotFound, $"Task {taskId} does not exist in project {query.ProjectId}.");
            }
        }

        var rows = await db.Sql("Message.List")
            .AddParameter("ProjectId", query.ProjectId).AddParameter("TaskId", query.TaskId)
            .AddParameter("All", query.All ? 1 : 0).AddParameter("After", query.After)
            .AddParameter("To", string.IsNullOrWhiteSpace(query.To) ? null : query.To)
            .AddParameter("Take", query.Top + 1)
            .GetModelListAsync<Message>(ct);

        var hasMore = rows.Count > query.Top;
        if (hasMore)
            rows.RemoveAt(rows.Count - 1);
        return new MessagePage
        {
            Messages = rows,
            LastMessageId = rows.Count > 0 ? rows[^1].Id : query.After,
            HasMore = hasMore,
        };
    }

    /// <summary>Message count and estimated unanswered questions of a task channel, inside the caller's transaction.</summary>
    internal static async Task<MessageTaskStats> TaskStatsAsync(DbScope tx, long taskId, CancellationToken ct) =>
        await tx.Sql("Message.TaskStats").AddParameter("TaskId", taskId).GetModelAsync<MessageTaskStats>(ct)
        ?? new MessageTaskStats();

    private static async Task RequireProjectAsync(DbScope tx, long projectId, CancellationToken ct)
    {
        if (await tx.Sql("Project.GetById").AddParameter("Id", projectId).GetModelAsync<Project>(ct) is null)
            throw new TaskFlowException(TaskFlowError.NotFound, $"Project {projectId} does not exist.");
    }
}
