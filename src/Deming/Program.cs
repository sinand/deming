using Deming.Cli;
using Deming.Server;

// One binary, two roles: "deming serve" hosts the server, anything else is the CLI client.
if (args.Length > 0 && args[0] == "serve")
{
    try
    {
        await DemingServer.RunAsync(args[1..]);
        return ExitCodes.Success;
    }
    catch (Deming.Server.Exposure.ExposureException ex)
    {
        Console.Error.WriteLine(ex.Message);
        return ExitCodes.Error;
    }
}

return CliApp.Run(args, Console.Out, Console.Error);
