using Factory.Cli.Abstractions;

namespace Factory.Cli;

/// <summary><c>factory doctor</c>: every credential and permission, pass or fail and the fix (contract, "Secrets, init and doctor").</summary>
public sealed class DoctorCommand(IDoctor doctor) : IStandaloneCommand
{
    public string Name => "doctor";

    public int Run(IReadOnlyList<string> args, TextReader stdin, TextWriter stdout, TextWriter stderr) => doctor.Run(stdout) ? 0 : 1;
}
