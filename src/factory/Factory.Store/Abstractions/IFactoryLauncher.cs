namespace Factory.Store.Abstractions;

/// <summary>deploy.ps1's Start-FactoryDetached: a factory.ps1 command in a process of its own that outlives this one.</summary>
public interface IFactoryLauncher
{
    void StartDetached(IReadOnlyList<string> args);
}
