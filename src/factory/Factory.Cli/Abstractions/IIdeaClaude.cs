namespace Factory.Cli.Abstractions;

/// <summary>One idea turn's claude process: the prompt on stdin, each stdout line handed over as it arrives.</summary>
public interface IIdeaClaude
{
    /// <returns>The exit code and the tail of stderr.</returns>
    (int Code, string Err) Run(string file, IReadOnlyList<string> args, string workDir, IReadOnlyDictionary<string, string?> env, string stdin, Action<string> line);
}
