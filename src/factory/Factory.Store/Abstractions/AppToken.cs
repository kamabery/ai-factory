namespace Factory.Store.Abstractions;

/// <summary>A token GitHub minted, and when it expires.</summary>
public sealed record AppToken(string Token, DateTimeOffset ExpiresAt);
