namespace Factory.Core;

/// <summary>A refused command: factory.ps1's <c>Fail</c>. Printed as "factory: {Message}" on stderr.</summary>
public sealed class FactoryException(string message, int exitCode = 1) : Exception(message)
{
    public int ExitCode { get; } = exitCode;

    /// <summary>The command saves the board before it refuses (read-reviews keeps the verdicts it read).</summary>
    public bool KeepChanges { get; init; }
}
