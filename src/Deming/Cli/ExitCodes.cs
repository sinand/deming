namespace Deming.Cli;

/// <summary>CLI exit codes. See docs/scope.md, "CLI behaviour".</summary>
public static class ExitCodes
{
    public const int Success = 0;
    public const int Error = 1;
    public const int NothingToDo = 3;
    public const int AlreadyExists = 4;
}
