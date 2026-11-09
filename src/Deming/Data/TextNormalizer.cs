using System.Globalization;
using System.Text;

namespace Deming.Data;

/// <summary>
/// Folds text for duplicate detection and search. SQLite's LIKE and NOCASE only fold ASCII;
/// Harness measured the cost: "DRIFT" did not match "drift", "kopru" did not match "köprü".
/// Rules: Turkish dotless/dotted i fold to "i", diacritics removed, lower case (invariant),
/// whitespace runs collapsed to one space, trimmed.
/// Registered on every connection as the SQL function <c>deming_normalize(text)</c>.
/// </summary>
public static class TextNormalizer
{
    public const string SqlFunctionName = "deming_normalize";

    public static string Normalize(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var decomposed = text.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        var pendingSpace = false;

        foreach (var ch in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark)
                continue;

            if (char.IsWhiteSpace(ch))
            {
                pendingSpace = sb.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                sb.Append(' ');
                pendingSpace = false;
            }

            // 'İ' already decomposed to 'I' + combining dot (dropped above); 'ı' has no decomposition.
            sb.Append(ch == 'ı' ? 'i' : char.ToLowerInvariant(ch));
        }

        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    /// <summary>
    /// Folds character by character with the same rules as <see cref="Normalize"/>, but keeps the
    /// length: whitespace becomes one space each and nothing is removed, so an index found in the
    /// result is the same index in <paramref name="text"/>. Search uses it to place a snippet around a
    /// match. A combining mark written as its own character stays (it has no base to fold to).
    /// </summary>
    public static string FoldKeepingLength(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var result = new char[text.Length];
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (char.IsWhiteSpace(ch))
                result[i] = ' ';
            else if (ch == 'ı')
                result[i] = 'i';
            else if (char.IsSurrogate(ch))
                result[i] = ch;
            else
            {
                var decomposed = ch.ToString().Normalize(NormalizationForm.FormD);
                var folded = ch;
                foreach (var part in decomposed)
                {
                    if (CharUnicodeInfo.GetUnicodeCategory(part) == UnicodeCategory.NonSpacingMark)
                        continue;
                    folded = part;
                    break;
                }
                result[i] = char.ToLowerInvariant(folded);
            }
        }
        return new string(result);
    }

    /// <summary>SQL-facing variant: NULL stays NULL so NOT NULL constraints still fire.</summary>
    internal static string? NormalizeOrNull(string? text) => text is null ? null : Normalize(text);
}
