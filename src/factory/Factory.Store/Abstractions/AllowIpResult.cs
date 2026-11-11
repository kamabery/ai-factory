namespace Factory.Store.Abstractions;

/// <summary>The IP allow-ip opened, the one it closed (null when none, or the same), and the storage account.</summary>
public sealed record AllowIpResult(string Ip, string? Removed, string Account);
