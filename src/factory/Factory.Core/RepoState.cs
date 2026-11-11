namespace Factory.Core;

/// <summary>A target on GitHub (contract, "Targets"): a repo that is missing, or empty (no main), is new.</summary>
public enum RepoState
{
    Existing,
    Missing,
    Empty,
}
