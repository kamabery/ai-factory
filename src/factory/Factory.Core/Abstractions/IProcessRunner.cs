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
}
