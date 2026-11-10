namespace Factory.Cli.Abstractions;

public sealed record CommandResult(int ExitCode, IReadOnlyList<string> Out, IReadOnlyList<string> Err)
{
    public static CommandResult Ok(params string[] lines) => new(0, lines, []);
}
