namespace Factory.Cli.Abstractions;

/// <summary>One of factory doctor's checks (contract, "Secrets, init and doctor").</summary>
public interface IDoctorCheck
{
    string Name { get; }

    /// <summary>The outcome, or null when the check does not apply here (hcp with no HCP settings).</summary>
    DoctorResult? Check();
}
