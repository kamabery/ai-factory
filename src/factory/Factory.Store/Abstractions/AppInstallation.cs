namespace Factory.Store.Abstractions;

/// <summary>One installation of the GitHub App: its id, the account it is on, all or selected repositories, and its permissions.</summary>
public sealed record AppInstallation(long Id, string Account, string Selection, IReadOnlyDictionary<string, string> Permissions);
