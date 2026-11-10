namespace Factory.Store.Abstractions;

/// <summary>One row of a container listing. ETags are bare (no quotes).</summary>
public sealed record BlobItem(string Name, string? VersionId, bool IsCurrent, long Length, DateTime LastModified, string? ETag, bool Leased);
