namespace Factory.Cli.Abstractions;

/// <summary>Runs every doctor check, for doctor itself and for init's last step.</summary>
public interface IDoctor
{
    /// <summary>Prints one line per check (and a fix line under a warn or fail); false when any check failed.</summary>
    bool Run(TextWriter output);
}
