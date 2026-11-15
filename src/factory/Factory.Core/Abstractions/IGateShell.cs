namespace Factory.Core.Abstractions;

/// <summary>
/// One gate command in the work dir (contract, "Gate"): a program and its arguments with no shell, or a shell line left from
/// before that rule through FACTORY_GATE_SHELL.
/// </summary>
public interface IGateShell
{
    ShellResult Run(GateCommand gate, string workingDirectory);
}
