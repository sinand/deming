namespace Deming.Data.Models;

/// <summary>A row of Sql/Search/Match.sql: the hit with its searchable fields whole (never returned).</summary>
public sealed class SearchRow
{
    public string Type { get; set; } = "";
    public long Id { get; set; }
    public long ProjectId { get; set; }
    public string ProjectName { get; set; } = "";
    public string Title { get; set; } = "";
    public string? Status { get; set; }
    public long? TaskId { get; set; }
    public DateTime CreatedAt { get; set; }
    public string? Field1 { get; set; }
    public string? Field2 { get; set; }
    public string? Field3 { get; set; }
}

/// <summary>
/// One search hit as the API returns it: narrow on purpose. The full text is read by id
/// (findings, decisions, tasks; a note through its task).
/// </summary>
public sealed class SearchResult
{
    /// <summary>finding | decision | note | task | message (<see cref="SearchTypes"/>).</summary>
    public string Type { get; init; } = "";
    public long Id { get; init; }
    public long ProjectId { get; init; }
    public string ProjectName { get; init; } = "";

    /// <summary>The row's title; a note carries its task's title, a message its sender's label.</summary>
    public string Title { get; init; } = "";

    /// <summary>Task or decision status; a note's kind (handoff | note); a message's channel (project | task-&lt;id&gt;); null for a finding.</summary>
    public string? Status { get; init; }

    /// <summary>The task a note belongs to, the task a finding or decision was recorded in, or the task whose channel a message was said in.</summary>
    public long? TaskId { get; init; }

    /// <summary>The field the snippet comes from (title, body, text, rationale, summary, caveats, touchedFiles, scope).</summary>
    public string MatchedIn { get; init; } = "";

    /// <summary>About <see cref="SearchService.SnippetLength"/> characters around the first match, whitespace collapsed.</summary>
    public string Snippet { get; init; } = "";

    public DateTime CreatedAt { get; init; }
}

/// <summary>The search response: hits newest first; <see cref="More"/> when the limit cut some off.</summary>
public sealed class SearchResponse
{
    public IReadOnlyList<SearchResult> Results { get; init; } = [];
    public bool More { get; init; }
}

/// <summary>
/// The searchable kinds and the fields each one searches besides its title, in the order
/// Sql/Search/Match.sql returns them as field1..field3. A new kind is one entry here and one
/// branch in the SQL.
/// </summary>
public static class SearchTypes
{
    public const string Finding = "finding";
    public const string Decision = "decision";
    public const string Note = "note";
    public const string Task = "task";
    public const string Message = "message";

    public static readonly IReadOnlyDictionary<string, string[]> Fields = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        [Finding] = ["body"],
        [Decision] = ["text", "rationale"],
        [Note] = ["summary", "caveats", "touchedFiles"],
        [Task] = ["scope"],
        [Message] = ["body"],
    };

    public static readonly IReadOnlyList<string> All = [Finding, Decision, Note, Task, Message];
}
