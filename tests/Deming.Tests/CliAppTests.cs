using Deming.Cli;

namespace Deming.Tests;

public class CliAppTests
{
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
