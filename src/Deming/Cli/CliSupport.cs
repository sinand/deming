using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Deming.Cli;

/// <summary>Everything the CLI takes from its surroundings, so tests can supply their own.</summary>
public sealed class CliContext
{
    public required TextWriter Out { get; init; }
    public required TextWriter Err { get; init; }
    public TextReader In { get; init; } = TextReader.Null;
    public Func<string, string?> Env { get; init; } = Environment.GetEnvironmentVariable;
    public string CurrentDirectory { get; init; } = Directory.GetCurrentDirectory();

    /// <summary>Test seam: the handler the CLI's HttpClient uses (so a test can trust its own certificate only). Not disposed by the CLI.</summary>
    public HttpMessageHandler? HttpHandler { get; init; }

    /// <summary>Test seam: the trust store 'deming trust' writes to (null result = this platform has none). Default: the real one.</summary>
    public Func<ITrustStore?>? TrustStore { get; init; }

    /// <summary>Test seam: where 'deming trust' looks for the certificate. Default: the data directory.</summary>
    public string? DataDirectory { get; init; }

    /// <summary>Test seam: the git program 'deming wt', 'merge' and 'wt-remove' run. Default: git on PATH.</summary>
    public string? GitExecutable { get; init; }

    /// <summary>Test seam: environment variables set (or, with a null value, removed) for each git child process.</summary>
    public IReadOnlyDictionary<string, string?>? GitEnvironment { get; init; }
}

/// <summary>A failure to report on stderr with an exit code; never a stack trace.</summary>
internal sealed class CliException(string message, int exitCode = ExitCodes.Error) : Exception(message)
{
    public int ExitCode { get; } = exitCode;
}

internal sealed class ParsedArgs
{
    public List<string> Positional { get; } = [];
    public Dictionary<string, string> Flags { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Value flags that came last, with nothing after them to take as the value.</summary>
    public HashSet<string> Dangling { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Splits the arguments into positional words and flags. Only the flags the command declares
    /// (<see cref="CliCommand.KnownFlags"/>) and --help / -h are accepted; any other --flag is an error
    /// before anything is sent, so a typo can never fall through as a value or be silently ignored.
    /// </summary>
    public static ParsedArgs Parse(IEnumerable<string> args, CliCommand command)
    {
        var valueFlags = command.ValueFlagNames;
        var parsed = new ParsedArgs();
        var queue = new Queue<string>(args);
        while (queue.Count > 0)
        {
            var arg = queue.Dequeue();
            if (arg is "-h")
                parsed.Flags["help"] = "";
            else if (arg.StartsWith("--", StringComparison.Ordinal) && arg.Length > 2)
            {
                var body = arg[2..];
                var eq = body.IndexOf('=');
                var name = eq > 0 ? body[..eq] : body;
                if (!name.Equals("help", StringComparison.OrdinalIgnoreCase) && !command.KnownFlags.Contains(name, StringComparer.OrdinalIgnoreCase))
                    throw new CliException($"unknown flag --{name}\n{command.HelpText()}");

                if (eq > 0)
                    parsed.Flags[name] = body[(eq + 1)..];
                else if (valueFlags.Contains(name, StringComparer.OrdinalIgnoreCase))
                {
                    if (queue.Count > 0)
                        parsed.Flags[body] = queue.Dequeue();
                    else
                    {
                        parsed.Flags[body] = "";
                        parsed.Dangling.Add(body);
                    }
                }
                else
                    parsed.Flags[body] = "";
            }
            else
                parsed.Positional.Add(arg);
        }
        return parsed;
    }

    public bool Has(string flag) => Flags.ContainsKey(flag);
    public string? Value(string flag) => Flags.TryGetValue(flag, out var v) && v.Length > 0 ? v : null;
}

internal sealed record ApiResponse(HttpStatusCode Status, string Body)
{
    public bool Ok => (int)Status is >= 200 and < 300;

    /// <summary>The refusal as the CLI reports it (message, exit code): duplicate_title is exit 4, anything else 1.</summary>
    public (string Message, int ExitCode) Describe()
    {
        string? code = null, message = null;
        long? existing = null;
        try
        {
            using var doc = JsonDocument.Parse(Body);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                code = root.TryGetProperty("error", out var c) ? c.GetString() : null;
                message = root.TryGetProperty("message", out var m) ? m.GetString() : null;
                existing = root.TryGetProperty("existingTaskId", out var e) && e.ValueKind == JsonValueKind.Number ? e.GetInt64() : null;
            }
        }
        catch (JsonException)
        {
        }

        if (code is null)
            return ($"error: HTTP {(int)Status} {Status}", ExitCodes.Error);
        if (code == "duplicate_title")
            return ($"already exists: task {existing}. {message}", ExitCodes.AlreadyExists);
        var hint = Status == HttpStatusCode.Unauthorized
            ? " Set DEMING_API_KEY, or run 'deming serve' once on this machine so it writes the key to the config file."
            : "";
        return ($"error: {code}: {message}{hint}", ExitCodes.Error);
    }
}

internal enum FieldKind
{
    /// <summary>Free text.</summary>
    Text,
    /// <summary>A whole number.</summary>
    Number,
    /// <summary>Task ids, comma separated on the command line.</summary>
    IdList,
    /// <summary>A true/false field; the bare flag means true. It also works next to a JSON argument.</summary>
    Switch,
    /// <summary>A line of help, not a field.</summary>
    Note,
}

/// <summary>
/// One field of a command's JSON body. The single source for the JSON name, the --kebab-case flag,
/// the type conversion and the help text.
/// </summary>
internal sealed record CliField(string Name, FieldKind Kind, string Doc)
{
    public static CliField Text(string name, string doc = "") => new(name, FieldKind.Text, doc);
    public static CliField Number(string name, string doc = "") => new(name, FieldKind.Number, doc);
    public static CliField IdList(string name, string doc = "") => new(name, FieldKind.IdList, doc);
    public static CliField Switch(string name, string doc = "") => new(name, FieldKind.Switch, doc);
    public static CliField Note(string text) => new("", FieldKind.Note, text);

    /// <summary>dependsOn -> depends-on.</summary>
    public string Flag
    {
        get
        {
            var sb = new StringBuilder();
            foreach (var c in Name)
            {
                if (char.IsUpper(c))
                {
                    sb.Append('-');
                    sb.Append(char.ToLowerInvariant(c));
                }
                else
                    sb.Append(c);
            }
            return sb.ToString();
        }
    }

    /// <summary>The value after the flag, as the JSON value the server expects.</summary>
    public JsonNode Convert(string raw)
    {
        switch (Kind)
        {
            case FieldKind.Number:
                return long.TryParse(raw.Trim(), out var number)
                    ? JsonValue.Create(number)
                    : throw new CliException($"--{Flag}: '{raw}' is not a whole number.");
            case FieldKind.IdList:
                var ids = new JsonArray();
                foreach (var part in raw.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                {
                    if (!long.TryParse(part, out var id) || id <= 0)
                        throw new CliException($"--{Flag}: '{part}' is not a task id (give ids separated by commas, like 3,4).");
                    ids.Add(id);
                }
                return ids;
            case FieldKind.Switch:
                return raw.Trim().ToLowerInvariant() switch
                {
                    "" or "true" => JsonValue.Create(true),
                    "false" => JsonValue.Create(false),
                    _ => throw new CliException($"--{Flag}: '{raw}' is not true or false."),
                };
            default:
                return JsonValue.Create(raw);
        }
    }
}

/// <summary>One CLI command: its names, usage, the fields its body takes and what it runs.</summary>
internal sealed record CliCommand(
    string Name, string[] Aliases, string Usage, string Summary, CliField[] Fields, bool NeedsSession, Func<CliRun, Task<int>> Run,
    string[]? Options = null, string[]? Switches = null)
{
    /// <summary>The fields that are real fields (not help notes).</summary>
    public IEnumerable<CliField> BodyFields => Fields.Where(f => f.Kind != FieldKind.Note);

    /// <summary>Flags that take a value (every field but the switches, plus the command's own value options).</summary>
    public string[] ValueFlagNames => BodyFields.Where(f => f.Kind != FieldKind.Switch).Select(f => f.Flag).Concat(Options ?? []).ToArray();

    /// <summary>Every flag the command accepts besides --help: its fields, its value options and its switches.</summary>
    public string[] KnownFlags => BodyFields.Select(f => f.Flag).Concat(Options ?? []).Concat(Switches ?? []).ToArray();

    public string HelpText()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Usage: {Usage}");
        sb.AppendLine($"  {Summary}");
        if (Aliases.Length > 0)
            sb.AppendLine($"  Also: {string.Join(", ", Aliases)}");
        if (NeedsSession)
            sb.AppendLine("  Needs a session id: DEMING_SESSION_ID, otherwise CLAUDE_CODE_SESSION_ID.");
        if (Fields.Length > 0)
        {
            sb.AppendLine("  Fields: give each as a flag, or give them all as one JSON object argument (or - to read it from stdin), not both:");
            foreach (var field in Fields)
            {
                if (field.Kind == FieldKind.Note)
                {
                    sb.AppendLine($"    {field.Doc}");
                    continue;
                }
                var flag = field.Kind switch
                {
                    FieldKind.Switch => $"--{field.Flag}",
                    FieldKind.Number => $"--{field.Flag} <number>",
                    FieldKind.IdList => $"--{field.Flag} <ids>",
                    _ => $"--{field.Flag} <text>",
                };
                var json = field.Flag == field.Name ? "" : $" (JSON: {field.Name})";
                sb.AppendLine($"    {flag}{json}  {field.Doc}".TrimEnd());
            }
        }
        return sb.ToString().TrimEnd();
    }
}

/// <summary>A running command: arguments, settings, and calls to the server.</summary>
internal sealed class CliRun(CliContext ctx, CliCommand command, ParsedArgs args) : IDisposable
{
    public const string DefaultUrl = Server.Exposure.ExposureSettings.DefaultUrl;
    private HttpClient? _http;

    public CliContext Ctx => ctx;
    public ParsedArgs Args => args;

    public string? Session =>
        new[] { "DEMING_SESSION_ID", "CLAUDE_CODE_SESSION_ID" }
            .Select(name => ctx.Env(name)?.Trim()).FirstOrDefault(v => !string.IsNullOrEmpty(v));

    public CliException Missing(string what) => new($"Missing {what}.\n{command.HelpText()}");

    public long RequireId(string what = "task id")
    {
        if (args.Positional.Count == 0)
            throw Missing(what);
        if (!long.TryParse(args.Positional[0], out var id) || id <= 0)
            throw new CliException($"'{args.Positional[0]}' is not a valid {what}.\n{command.HelpText()}");
        return id;
    }

    /// <summary>
    /// The command's body: the JSON object at <paramref name="index"/> (or stdin for -), or the fields given as flags.
    /// Both together is an error. A switch (like --takeover) is the exception: it also goes with a JSON argument,
    /// as long as the JSON does not hold the field itself.
    /// </summary>
    /// <param name="allowEmpty">The command has another source for the body, so no fields at all is fine.</param>
    public async Task<JsonObject> JsonArg(int index, bool allowEmpty = false)
    {
        var fields = new JsonObject();
        var switches = new JsonObject();
        foreach (var field in command.BodyFields)
        {
            if (!args.Flags.TryGetValue(field.Flag, out var raw))
                continue;
            if (args.Dangling.Contains(field.Flag))
                throw new CliException($"--{field.Flag} needs a value.\n{command.HelpText()}");
            (field.Kind == FieldKind.Switch ? switches : fields)[field.Name] = field.Convert(raw);
        }

        JsonObject body;
        if (args.Positional.Count > index)
        {
            if (fields.Count > 0)
                throw new CliException($"Give the fields as flags or as a JSON argument, not both: {string.Join(", ", fields.Select(f => "--" + command.BodyFields.First(b => b.Name == f.Key).Flag))} came with a JSON argument.\n{command.HelpText()}");
            body = await ParseJson(args.Positional[index]);
        }
        else if (fields.Count > 0 || switches.Count > 0 || allowEmpty)
            body = fields;
        else
            throw Missing("fields (give them as flags, or as a JSON argument)");

        foreach (var (name, value) in switches)
        {
            if (body.ContainsKey(name))
                throw new CliException($"'{name}' is in the JSON argument and also given as a flag; give it once.\n{command.HelpText()}");
            body[name] = value?.DeepClone();
        }
        return body;
    }

    private async Task<JsonObject> ParseJson(string text)
    {
        if (text == "-")
            text = await ctx.In.ReadToEndAsync();
        try
        {
            if (JsonNode.Parse(text) is JsonObject obj)
                return obj;
            throw new CliException($"The argument must be a JSON object.\n{command.HelpText()}");
        }
        catch (JsonException ex)
        {
            throw new CliException($"The argument is not valid JSON: {ex.Message}\nThe shell may have eaten the double quotes (Windows PowerShell 5.1 does): give the fields as flags instead, or send the JSON on stdin with '-'.\n{command.HelpText()}");
        }
    }

    /// <summary>block and cancel: the --reason flag, a JSON object, or the reason as plain words.</summary>
    public async Task<JsonObject> ReasonArg()
    {
        if (args.Positional.Count <= 1)
            return await JsonArg(1);
        var first = args.Positional[1];
        if (first == "-" || first.TrimStart().StartsWith('{'))
            return await JsonArg(1);
        if (args.Has("reason"))
            throw new CliException($"Give the reason as plain words or as --reason, not both.\n{command.HelpText()}");
        return new JsonObject { ["reason"] = string.Join(' ', args.Positional.Skip(1)) };
    }

    public async Task<ApiResponse> Send(HttpMethod method, string path, JsonNode? body = null)
    {
        var session = Session;
        if (command.NeedsSession && session is null)
            throw new CliException("No session id: set DEMING_SESSION_ID (or CLAUDE_CODE_SESSION_ID). Nothing was sent.");

        var http = Http();
        using var request = new HttpRequestMessage(method, path);
        if (session is not null)
            request.Headers.Add("X-Session-Id", session);
        if (body is not null)
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await http.SendAsync(request);
        return new ApiResponse(response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private HttpClient Http()
    {
        if (_http is not null)
            return _http;

        var configPath = UserConfig.DefaultPath(ctx.Env);
        UserConfig.Values file;
        try
        {
            file = UserConfig.Read(configPath);
        }
        catch (InvalidOperationException ex)
        {
            throw new CliException(ex.Message);
        }

        var key = Pick(ctx.Env("DEMING_API_KEY")) ?? file.ApiKey
            ?? throw new CliException("No API key. Run 'deming serve' once on this machine (it writes the key to the config file) or set DEMING_API_KEY.");
        var url = Pick(ctx.Env("DEMING_URL")) ?? file.Url ?? DefaultUrl;

        if (!Uri.TryCreate(url.TrimEnd('/') + "/", UriKind.Absolute, out var address) || address.Scheme != Uri.UriSchemeHttps)
            throw new CliException($"The server address '{url}' is not an https URL: http is not allowed, use https (default {DefaultUrl}). DEMING_URL or 'url' in the config file sets it.");

        // The certificate 'deming serve' generated is trusted by its fingerprint, anything else by the system.
        var pinned = CertificatePin.Parse(file.CertificateSha256, configPath);
        _http = ctx.HttpHandler is { } handler
            ? new HttpClient(handler, disposeHandler: false)
            : new HttpClient(CertificatePin.Handler(pinned), disposeHandler: true);
        _http.BaseAddress = address;
        _http.Timeout = TimeSpan.FromSeconds(30);
        _http.DefaultRequestHeaders.Add("X-Api-Key", key);
        return _http;
    }

    private static string? Pick(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>Server URL for messages (never the key).</summary>
    public string ServerUrl => _http?.BaseAddress?.ToString().TrimEnd('/') ?? DefaultUrl;

    // ---- project from the .deming file

    /// <summary>The project name from the nearest .deming file upward from the working directory, never the directory name.</summary>
    public string ProjectName()
    {
        for (var dir = new DirectoryInfo(ctx.CurrentDirectory); dir is not null; dir = dir.Parent)
        {
            var file = Path.Combine(dir.FullName, ".deming");
            if (!File.Exists(file))
                continue;
            var name = File.ReadAllLines(file, Encoding.UTF8)
                .Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0 && !l.StartsWith('#'));
            if (name is not null)
                return name;
        }
        throw new CliException("No .deming file found. Create it at the repository root with: deming init <project name>");
    }

    public async Task<long> ProjectId()
    {
        var name = ProjectName();
        var response = await Send(HttpMethod.Get, "api/projects?name=" + Uri.EscapeDataString(name));
        if (!response.Ok)
        {
            var (message, code) = response.Describe();
            throw new CliException(message, code);
        }
        using var doc = JsonDocument.Parse(response.Body);
        if (doc.RootElement.GetArrayLength() == 0)
            throw new CliException($"Project '{name}' (from .deming) does not exist on the server. Create it with: deming init {name}");
        return doc.RootElement[0].GetProperty("id").GetInt64();
    }

    /// <summary>Print a successful body, or report the refusal; returns the exit code.</summary>
    public int Finish(ApiResponse response)
    {
        if (response.Ok)
        {
            if (response.Body.Length > 0)
                ctx.Out.WriteLine(response.Body);
            return ExitCodes.Success;
        }
        var (message, code) = response.Describe();
        ctx.Err.WriteLine(message);
        return code;
    }

    public void Dispose() => _http?.Dispose();
}
