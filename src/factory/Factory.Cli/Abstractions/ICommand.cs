namespace Factory.Cli.Abstractions;

/// <summary>A factory.ps1 command, ported.</summary>
public interface ICommand
{
    string Name { get; }
    CommandResult Run(CommandArgs args);
}
