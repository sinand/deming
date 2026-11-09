using System.Reflection;
using System.Text.RegularExpressions;

namespace Deming.Data;

/// <summary>
/// The .sql files embedded in the binary (see Data/README.md and Deming.csproj).
///   Data/Sql/&lt;Area&gt;/&lt;Operation&gt;.sql  -> named query "Area.Operation"
///   Data/Schema/NNNN_name.sql            -> schema script number NNNN
/// Naming is enforced on first use: a misnamed file stops the process instead of being skipped.
/// </summary>
public static partial class SqlCatalog
{
    private const string QueryPrefix = "sql/";
    private const string SchemaPrefix = "schema/";

    [GeneratedRegex(@"^[A-Z][A-Za-z0-9]*\.[A-Z][A-Za-z0-9]*$")]
    private static partial Regex QueryNamePattern();

    [GeneratedRegex(@"^(?<n>\d{4})_[a-z0-9_]+\.sql$")]
    private static partial Regex SchemaNamePattern();

    private static readonly Lazy<IReadOnlyDictionary<string, string>> LazyQueries = new(LoadQueries);
    private static readonly Lazy<IReadOnlyList<SchemaScript>> LazySchema = new(LoadSchema);

    public static IReadOnlyDictionary<string, string> Queries => LazyQueries.Value;
    public static IReadOnlyList<SchemaScript> Schema => LazySchema.Value;

    public static string Get(string name) =>
        Queries.TryGetValue(name, out var sql)
            ? sql
            : throw new KeyNotFoundException(
                $"No embedded query '{name}'. Expected file src/Deming/Data/Sql/{name.Replace('.', '/')}.sql.");

    private static Assembly Assembly => typeof(SqlCatalog).Assembly;

    private static string Read(string resource)
    {
        using var stream = Assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"Embedded resource '{resource}' not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static IReadOnlyDictionary<string, string> LoadQueries()
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var resource in Assembly.GetManifestResourceNames())
        {
            if (!resource.StartsWith(QueryPrefix, StringComparison.Ordinal))
                continue;

            var name = resource[QueryPrefix.Length..^".sql".Length].Replace('\\', '.').Replace('/', '.');
            if (!QueryNamePattern().IsMatch(name))
                throw new InvalidOperationException(
                    $"Embedded query '{resource}' does not follow Data/Sql/<Area>/<Operation>.sql (PascalCase, one folder level).");
            result.Add(name, Read(resource));
        }
        return result;
    }

    private static IReadOnlyList<SchemaScript> LoadSchema()
    {
        var scripts = new List<SchemaScript>();
        foreach (var resource in Assembly.GetManifestResourceNames())
        {
            if (!resource.StartsWith(SchemaPrefix, StringComparison.Ordinal))
                continue;

            var file = resource[SchemaPrefix.Length..];
            var match = SchemaNamePattern().Match(file);
            if (!match.Success)
                throw new InvalidOperationException($"Schema script '{file}' does not follow NNNN_name.sql.");
            scripts.Add(new SchemaScript(int.Parse(match.Groups["n"].Value), file, Read(resource)));
        }

        scripts.Sort((a, b) => a.Version.CompareTo(b.Version));
        for (var i = 0; i < scripts.Count; i++)
        {
            if (scripts[i].Version != i + 1)
                throw new InvalidOperationException(
                    $"Schema scripts must be numbered 0001, 0002, ... without gaps or duplicates; found '{scripts[i].Name}' at position {i + 1}.");
        }
        return scripts;
    }
}

public sealed record SchemaScript(int Version, string Name, string Sql);
