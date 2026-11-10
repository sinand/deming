using System.Text;

namespace Deming.Cli;

/// <summary>
/// The CLI's standard streams as UTF-8, whatever the console's code page is. An agent reads the output from a
/// pipe and writes JSON to stdin as UTF-8; left to the console's code page (437, 1252, 1254 on Windows) the
/// same bytes were decoded as that page, and a title like "İş" was stored as mojibake or printed as "Is".
/// </summary>
internal static class ConsoleStreams
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>Stdin: UTF-8 when it is a pipe or a file (a byte order mark is skipped); the console's own reader when a person types.</summary>
    public static TextReader In() => Console.IsInputRedirected
        ? new StreamReader(Console.OpenStandardInput(), Utf8, detectEncodingFromByteOrderMarks: true)
        : Console.In;

    public static TextWriter Out() => Console.IsOutputRedirected ? Writer(Console.OpenStandardOutput()) : Console.Out;

    public static TextWriter Err() => Console.IsErrorRedirected ? Writer(Console.OpenStandardError()) : Console.Error;

    private static StreamWriter Writer(Stream stream) => new(stream, Utf8) { AutoFlush = true };

    /// <summary>
    /// When stdout or stderr is a real Windows console, switches its output code page to UTF-8 for this run and
    /// puts the previous one back on dispose, so the user's terminal is left as it was. Call it before
    /// <see cref="Out"/> and <see cref="Err"/>. Elsewhere (pipes, Unix) it does nothing.
    /// </summary>
    public static IDisposable Utf8Console()
    {
        if (!OperatingSystem.IsWindows() || (Console.IsOutputRedirected && Console.IsErrorRedirected))
            return Restore.Nothing;
        try
        {
            var previous = Console.OutputEncoding;
            if (previous.CodePage == Utf8.CodePage)
                return Restore.Nothing;
            Console.OutputEncoding = Utf8;
            return new Restore(previous);
        }
        catch (IOException)
        {
            // No console to switch (a detached process): the streams above still write UTF-8.
            return Restore.Nothing;
        }
    }

    private sealed class Restore(Encoding? previous) : IDisposable
    {
        public static readonly Restore Nothing = new(null);

        public void Dispose()
        {
            if (previous is null)
                return;
            try
            {
                Console.OutputEncoding = previous;
            }
            catch (IOException)
            {
            }
        }
    }
}
