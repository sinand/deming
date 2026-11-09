using System.Text;
using System.Text.Json;
using Deming.Data.Models;

namespace Deming.Data.Services;

/// <summary>
/// Search over findings (title, body), decisions (title, text, rationale), notes (summary, caveats,
/// touched files; block and cancel reasons are notes too) and tasks (title, scope).
/// The query is split on whitespace and every term must occur; terms and text are folded by
/// <see cref="TextNormalizer"/> (case, Turkish i, diacritics). The response is narrow: a snippet
/// around the first match, never the full text, and it is never part of the <c>next</c> response.
/// No full-text index: the data is small and the fold has to match <see cref="TextNormalizer"/>,
/// which FTS5's tokenizers do not.
/// </summary>
public sealed class SearchService(DemingDb db)
{
    public const int DefaultTop = 20;
    public const int MaxTop = 100;
    public const int MaxTerms = 20;
    public const int SnippetLength = 160;

    /// <summary>How far before the first match the snippet starts, so the match has some lead-in.</summary>
    private const int LeadIn = 50;

    /// <param name="projectId">One project (an unknown one is NotFound).</param>
    /// <param name="allProjects">Every project, inactive ones too: their history is kept so that it can be found.
    /// Exactly one of <paramref name="projectId"/> and this must be given.</param>
    /// <param name="type">One of <see cref="SearchTypes.All"/>; null or empty searches every kind.</param>
    /// <param name="top">1 to <see cref="MaxTop"/>; null is <see cref="DefaultTop"/>.</param>
    public async Task<SearchResponse> SearchAsync(string? query, long? projectId, bool allProjects, string? type, int? top, CancellationToken ct = default)
    {
        if (projectId is null != allProjects)
            throw new TaskFlowException(TaskFlowError.InvalidInput,
                "Give project=<id> to search one project, or all=true to search every project (not both).");

        var terms = Terms(query);
        if (terms.Count == 0)
            throw new TaskFlowException(TaskFlowError.InvalidInput, "Give the words to search for (q).");
        if (terms.Count > MaxTerms)
            throw new TaskFlowException(TaskFlowError.InvalidInput, $"Search with at most {MaxTerms} words.");

        type = string.IsNullOrWhiteSpace(type) ? null : type.Trim().ToLowerInvariant();
        if (type is not null && !SearchTypes.All.Contains(type))
            throw new TaskFlowException(TaskFlowError.InvalidInput, $"type must be one of: {string.Join(", ", SearchTypes.All)}.");

        var limit = top ?? DefaultTop;
        if (limit is < 1 or > MaxTop)
            throw new TaskFlowException(TaskFlowError.InvalidInput, $"top must be between 1 and {MaxTop}.");

        var rows = await db.InTransactionAsync<IReadOnlyList<SearchRow>>(async tx =>
        {
            if (projectId is { } id && await tx.Sql("Project.GetById").AddParameter("Id", id).GetModelAsync<Project>(ct) is null)
                throw new TaskFlowException(TaskFlowError.NotFound, $"Project {id} does not exist.");
            return await tx.Sql("Search.Match")
                .AddParameter("Terms", JsonSerializer.Serialize(terms)).AddParameter("ProjectId", projectId)
                .AddParameter("Type", type).AddParameter("Limit", limit + 1)
                .GetModelListAsync<SearchRow>(ct);
        }, ct);

        return new SearchResponse
        {
            Results = rows.Take(limit).Select(r => ToResult(r, terms)).ToList(),
            More = rows.Count > limit,
        };
    }

    /// <summary>The query's words, folded, without blanks and repeats.</summary>
    internal static IReadOnlyList<string> Terms(string? query) =>
        string.IsNullOrWhiteSpace(query)
            ? []
            : query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                .Select(TextNormalizer.Normalize)
                .Where(t => t.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .ToList();

    private static SearchResult ToResult(SearchRow row, IReadOnlyList<string> terms)
    {
        var names = SearchTypes.Fields[row.Type];
        var values = new[] { row.Field1, row.Field2, row.Field3 };

        // Candidates: the fields after the title first (the title is in the result anyway), then the title.
        var candidates = new List<(string Name, string Text)>();
        for (var i = 0; i < names.Length; i++)
        {
            if (!string.IsNullOrWhiteSpace(values[i]))
                candidates.Add((names[i], values[i]!));
        }
        candidates.Add(("title", row.Title));

        // The field holding the most terms; on a tie the earlier one.
        (string Name, string Text, int Position) best = ("", "", -1);
        var bestCount = 0;
        foreach (var (name, text) in candidates)
        {
            var folded = TextNormalizer.FoldKeepingLength(text);
            var count = 0;
            var first = int.MaxValue;
            foreach (var term in terms)
            {
                var at = folded.IndexOf(term, StringComparison.Ordinal);
                if (at < 0)
                    continue;
                count++;
                first = Math.Min(first, at);
            }
            if (count > bestCount)
            {
                bestCount = count;
                best = (name, text, first);
            }
        }

        string matchedIn, snippet;
        if (best.Name is "title" or "")
        {
            // Matched only in the title (or across fields): show the start of the first field for context.
            matchedIn = best.Name is "" ? candidates[0].Name : "title";
            snippet = Snippet(candidates[0].Text, 0);
        }
        else
        {
            matchedIn = best.Name;
            snippet = Snippet(best.Text, best.Position);
        }

        return new SearchResult
        {
            Type = row.Type,
            Id = row.Id,
            ProjectId = row.ProjectId,
            ProjectName = row.ProjectName,
            Title = row.Title,
            Status = row.Status,
            TaskId = row.TaskId,
            MatchedIn = matchedIn,
            Snippet = snippet,
            CreatedAt = row.CreatedAt,
        };
    }

    /// <summary>
    /// About <see cref="SnippetLength"/> characters of <paramref name="text"/> starting a little before
    /// <paramref name="position"/>, cut at word boundaries where one is near, whitespace collapsed,
    /// with an ellipsis on each side that was cut.
    /// </summary>
    internal static string Snippet(string text, int position)
    {
        if (text.Length == 0)
            return "";
        position = Math.Clamp(position, 0, text.Length - 1);
        var start = Math.Clamp(position - LeadIn, 0, text.Length);
        if (start > 0)
        {
            var space = text.IndexOf(' ', start, Math.Min(15, position - start));
            if (space >= 0)
                start = space + 1;
        }

        var end = Math.Min(text.Length, start + SnippetLength);
        if (end < text.Length)
        {
            var space = text.LastIndexOf(' ', end - 1, Math.Min(20, end - start));
            if (space > Math.Max(start, position))
                end = space;
        }
        // Never split a surrogate pair.
        if (start > 0 && char.IsLowSurrogate(text[start]))
            start++;
        if (end < text.Length && end > start && char.IsHighSurrogate(text[end - 1]))
            end--;

        var sb = new StringBuilder(end - start + 2);
        if (start > 0)
            sb.Append('…');
        var pendingSpace = false;
        for (var i = start; i < end; i++)
        {
            if (char.IsWhiteSpace(text[i]))
            {
                pendingSpace = sb.Length > (start > 0 ? 1 : 0);
                continue;
            }
            if (pendingSpace)
                sb.Append(' ');
            pendingSpace = false;
            sb.Append(text[i]);
        }
        if (end < text.Length)
            sb.Append('…');
        return sb.ToString();
    }
}
