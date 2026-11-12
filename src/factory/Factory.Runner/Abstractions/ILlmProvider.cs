namespace Factory.Runner.Abstractions;

/// <summary>llm.ps1: the run mode decides the provider, never a job (contract, "Run mode and providers").</summary>
public interface ILlmProvider
{
    /// <summary>
    /// The claude child's environment for the run mode: every variable that could route claude elsewhere removed
    /// (null), and in cloud mode the Foundry resource and model deployments. Refuses (a FactoryException) on a mode
    /// that is neither local nor cloud, and on cloud mode with no FACTORY_FOUNDRY_RESOURCE.
    /// </summary>
    IReadOnlyDictionary<string, string?> ChildEnvironment();
}
