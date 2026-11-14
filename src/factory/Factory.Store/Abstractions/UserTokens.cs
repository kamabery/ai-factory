namespace Factory.Store.Abstractions;

/// <summary>The App's user token, its refresh token, and when it expires (null: it does not).</summary>
public sealed record UserTokens(string Token, string? Refresh, DateTimeOffset? ExpiresAt);
