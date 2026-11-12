namespace Factory.Cli.Abstractions;

/// <summary>
/// A command that runs without the board or its lock (idea-turn, idea-mcp): it lasts minutes, and holding the board for
/// that long would keep every other command waiting. It reads and writes its own files only.
/// </summary>
public interface IStandaloneCommand
{
    string Name { get; }

    /// <summary>Runs the command with the arguments after its name; returns the exit code.</summary>
    int Run(IReadOnlyList<string> args, TextReader stdin, TextWriter stdout, TextWriter stderr);
}
