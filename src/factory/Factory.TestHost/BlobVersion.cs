namespace Factory.TestHost;

/// <summary>A non-current version the Blob stub keeps when a blob is overwritten or deleted, as versioning does.</summary>
public sealed record BlobVersion(string Name, string VersionId, byte[] Bytes, string Type, DateTime Modified);
