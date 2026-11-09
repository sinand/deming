using System.Text;
using System.Text.Json.Serialization;
using Deming.Data;
using Deming.Data.Models;
using Deming.Data.Services;

namespace Deming.Server.Api;

/// <summary>
/// The body of every API refusal: <c>error</c> is a stable snake_case code, <c>message</c> says what to do.
/// Status codes: 400 bad input, 401 key missing/wrong, 404 not found, 409 state conflict.
/// </summary>
public sealed class ApiError
{
    public required string Error { get; init; }
    public required string Message { get; init; }

    /// <summary>duplicate_title: the unfinished task that already has this title.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? ExistingTaskId { get; init; }

    /// <summary>dependencies_pending: what the task still waits for.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<TaskDependencyInfo>? PendingDependencies { get; init; }

    /// <summary>A violated database rule (constraint name or trigger code).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Constraint { get; init; }
}

/// <summary>Turns exceptions into (status, <see cref="ApiError"/>). Nothing is swallowed: unknown ones become 500 and are logged by the caller.</summary>
internal static class ApiErrors
{
    public static (int Status, ApiError Body) From(TaskFlowException ex) => (ex.HttpStatus, new ApiError
    {
        Error = Code(ex.Error),
        Message = ex.Message,
        ExistingTaskId = ex.ExistingTaskId,
        PendingDependencies = ex.Error == TaskFlowError.DependenciesPending
            ? ex.PendingDependencies.Select(t => new TaskDependencyInfo { Id = t.Id, Title = t.Title, Status = t.Status }).ToList()
            : null,
    });

    /// <summary>
    /// Database rules are not repeated in C#; the violated one is translated here.
    /// Trigger codes become the error code themselves; a check becomes a 400 with the rule's name.
    /// </summary>
    public static (int Status, ApiError Body) From(ConstraintViolationException ex)
    {
        var (status, code, message) = ex.Kind switch
        {
            ConstraintKind.Unique => (409, "already_exists", "A record with the same unique value already exists."),
            ConstraintKind.Trigger => (409, ex.Constraint, $"Refused by rule '{ex.Constraint}'."),
            ConstraintKind.ForeignKey => (400, "invalid_reference", "A referenced record does not exist."),
            _ => (400, "invalid_input", $"Refused by rule '{ex.Constraint}'."),
        };

        message = ex.Constraint switch
        {
            "ck_decision_text" => $"Decision text is required and at most {NoteLimits.DecisionTextMax} characters.",
            "ck_decision_title" => "Decision title is required.",
            "ck_finding_title" => $"Finding title is required and at most {NoteLimits.FindingTitleMax} characters.",
            "ck_finding_body" => $"Finding body is required and at most {NoteLimits.FindingBodyMax} characters.",
            "finding_task_other_project" => "The finding's task belongs to another project.",
            "finding_decision_other_project" => "The finding's decision belongs to another project.",
            "ck_project_name_not_blank" => "Project name is required.",
            "ck_project_repo_path_not_blank" => "Repo path must not be blank.",
            "project_inactive" => "The project is inactive; set isActive to true to use it again.",
            "decision_already_superseded" => "This decision was already superseded.",
            "ck_message_sender" => $"Sender label is required: lowercase ASCII letters, digits, '.', '_' or '-', at most {MessageLimits.LabelMax} characters.",
            "ck_message_recipient" => $"Recipient label must be lowercase ASCII letters, digits, '.', '_' or '-', at most {MessageLimits.LabelMax} characters.",
            "ck_message_body" => $"Message body is required and at most {MessageLimits.BodyTarget} characters.",
            "message_task_other_project" => "The message's task does not exist in this project.",
            "message_reply_other_channel" => "A reply must answer a message of the same channel (same project and task).",
            "message_task_finished" => "The task is done or cancelled; its channel is read-only.",
            _ => message,
        };
        // A wrong reference in the request is bad input, not a state conflict.
        if (ex.Constraint is "message_task_other_project" or "message_reply_other_channel")
            status = 400;
        if (ex.Kind == ConstraintKind.Unique && ex.Constraint.Contains("project.name_normalized", StringComparison.Ordinal))
            (code, message) = ("project_exists", "A project with this name already exists.");

        return (status, new ApiError { Error = code, Message = message, Constraint = ex.Constraint });
    }

    public static ApiError Unauthorized(string code, string message) => new() { Error = code, Message = message };

    public static ApiError BadRequest(string message) => new() { Error = "bad_request", Message = message };

    public static ApiError NotFound(string message) => new() { Error = "not_found", Message = message };

    /// <summary>TaskFlowError.DuplicateTitle -> "duplicate_title".</summary>
    private static string Code(TaskFlowError error)
    {
        var name = error.ToString();
        var sb = new StringBuilder(name.Length + 4);
        for (var i = 0; i < name.Length; i++)
        {
            if (char.IsUpper(name[i]) && i > 0)
                sb.Append('_');
            sb.Append(char.ToLowerInvariant(name[i]));
        }
        return sb.ToString();
    }
}
