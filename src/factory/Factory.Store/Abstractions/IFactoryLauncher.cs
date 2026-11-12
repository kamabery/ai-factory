namespace Factory.Store.Abstractions;

/// <summary>deploy.ps1's Start-FactoryDetached: a factory.ps1 command in a process of its own that outlives this one.</summary>
public interface IFactoryLauncher
{
    void StartDetached(IReadOnlyList<string> args);

    /// <summary>Any script in factory/scripts (runner.ps1, idea-turn.ps1), detached the same way.</summary>
    void StartScript(string script, IReadOnlyList<string> args);
}
