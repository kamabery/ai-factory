namespace Factory.Store.Abstractions;

/// <summary>One variable of an HCP variable set: its category (terraform or env), key, value and whether it is sensitive.</summary>
public sealed record HcpVar(string Category, string Key, string? Value, bool Sensitive);
