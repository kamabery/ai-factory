namespace Factory.Core;

/// <summary>A refused command: factory.ps1's <c>Fail</c>. Printed as "factory: {Message}" on stderr.</summary>
public sealed class FactoryException(string message, int exitCode = 1) : Exception(message)
{
    public int ExitCode { get; } = exitCode;
}

/// <summary>The file system, as far as the factory's state needs it.</summary>
public interface IFileStore
{
    /// <summary>The file's text, or null when it does not exist.</summary>
    string? ReadText(string path);

    void EnsureDirectory(string path);
}

public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr);

public interface IProcessRunner
{
    ProcessResult Run(string fileName, IReadOnlyList<string> arguments, string workingDirectory);
}

public interface IGitClient
{
    /// <summary>The absolute git common dir of the repository at <paramref name="workingDirectory"/>, or null outside one.</summary>
    string? CommonDir(string workingDirectory);
}
