namespace Factory.Store.Abstractions;

/// <summary>A <c>factory</c> command in a process of its own that outlives this one (contract, "Implementations").</summary>
public interface IFactoryLauncher
{
    /// <summary><c>dotnet &lt;factory.dll&gt; &lt;args&gt;</c>, detached: <c>destroy</c>, <c>sync-prs</c>, <c>runner</c>, <c>idea-turn</c>.</summary>
    void StartDetached(IReadOnlyList<string> args);
}
