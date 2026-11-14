namespace Factory.Runner.Abstractions;

/// <summary>
/// The factory CLI, as the runner calls it. Every state change goes through it (contract, "Factory rules").
/// </summary>
public interface IFactoryCommands
{
    FactoryResult Run(params string[] args);
}
