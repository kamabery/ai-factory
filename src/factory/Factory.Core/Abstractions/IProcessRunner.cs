namespace Factory.Core.Abstractions;

public interface IProcessRunner
{
    ProcessResult Run(string fileName, IReadOnlyList<string> arguments, string workingDirectory);

    /// <summary>
    /// The same, with <paramref name="stdin"/> written to the child (and then closed), and <paramref name="environment"/>
    /// set for this one child only: a null value removes the variable. A secret goes in either, never in an argument.
    /// </summary>
    ProcessResult Run(string fileName, IReadOnlyList<string> arguments, string workingDirectory, string? stdin,
        IReadOnlyDictionary<string, string?>? environment);

    /// <summary>
    /// A child on this process's own console (stdin, stdout and stderr inherited), for a human to use: the dashboard, the
    /// runner, an idea session. Waits for it and returns its exit code; <paramref name="environment"/> is as for Run.
    /// </summary>
    int RunAttached(string fileName, IReadOnlyList<string> arguments, string workingDirectory, IReadOnlyDictionary<string, string?>? environment);
}
