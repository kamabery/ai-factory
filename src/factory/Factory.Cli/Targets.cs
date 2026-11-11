using Factory.Core;
using Factory.Store.Abstractions;

namespace Factory.Cli;

/// <summary>factory.ps1's Test-NewTarget: a missing repo, or one with no main, gets scaffolded by a bootstrap job.</summary>
public static class Targets
{
    /// <summary>True for a new target; refuses as <c>&lt;what&gt; refused: &lt;GitHub's error&gt;</c>.</summary>
    public static bool IsNew(IGitHub github, string target, string what)
    {
        try { return github.Lookup(target) != RepoState.Existing; }
        catch (FactoryException e) { throw new FactoryException($"{what} refused: {e.Message}"); }
    }
}
