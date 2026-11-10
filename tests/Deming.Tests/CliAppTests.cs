using Deming.Cli;

namespace Deming.Tests;

public class CliAppTests
{
    /// <summary>
    /// The real process, through pipes: what an agent sees. Before ConsoleStreams the bytes followed the
    /// console's code page (437, 1252), so "İş" came out as "Is" and UTF-8 JSON on stdin was stored as mojibake.
    /// </summary>
    [Fact]
    public void The_process_writes_utf8_to_a_pipe_whatever_the_console_code_page_is()
    {
        var start = new System.Diagnostics.ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "deming.dll"));
        start.ArgumentList.Add("frobnicate-İşığüöç");

        using var process = System.Diagnostics.Process.Start(start)!;
        var stderr = process.StandardError.ReadToEnd();
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();

        Assert.Equal(ExitCodes.Error, process.ExitCode);
        Assert.Contains("frobnicate-İşığüöç", stderr);
    }

    [Theory]
    [InlineData]
    [InlineData("help")]
    [InlineData("--help")]
    public void Usage_is_printed_and_exits_zero(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var code = CliApp.Run(args, stdout, stderr);

        Assert.Equal(ExitCodes.Success, code);
        Assert.Contains("deming serve", stdout.ToString());
        Assert.Empty(stderr.ToString());
    }

    [Fact]
    public void Version_prints_informational_version()
    {
        var stdout = new StringWriter();

        var code = CliApp.Run(["version"], stdout, TextWriter.Null);

        Assert.Equal(ExitCodes.Success, code);
        Assert.StartsWith("0.1.0", stdout.ToString().Trim());
    }

    [Fact]
    public void Unknown_command_fails_with_error_code()
    {
        var stderr = new StringWriter();

        var code = CliApp.Run(["frobnicate"], TextWriter.Null, stderr);

        Assert.Equal(ExitCodes.Error, code);
        Assert.Contains("Unknown command: frobnicate", stderr.ToString());
    }
}
