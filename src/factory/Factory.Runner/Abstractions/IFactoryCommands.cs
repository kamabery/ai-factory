namespace Factory.Runner.Abstractions;

/// <summary>
/// factory.ps1, as the runner calls it. Every state change goes through it (contract, "Factory rules"); a ported command
/// runs on .NET from there when FACTORY_IMPL=dotnet, and the deploy commands in PowerShell until spec 09.
/// </summary>
public interface IFactoryCommands
{
    FactoryResult Run(params string[] args);
}
