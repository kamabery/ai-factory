namespace Factory.Core.Abstractions;

/// <summary>factory.ps1's Invoke-GateCommand: one gate command, through <c>pwsh -NoProfile -Command</c>, in the work dir.</summary>
public interface IGateShell
{
    ShellResult Run(string command, string workingDirectory);
}
