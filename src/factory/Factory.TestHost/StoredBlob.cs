namespace Factory.TestHost;

/// <summary>One current blob on the Blob stub, with its lease if it has one.</summary>
public sealed class StoredBlob
{
    public required string Type { get; set; }
    public required byte[] Bytes { get; set; }
    public required string ETag { get; set; }
    public required DateTime Modified { get; set; }
    public required string VersionId { get; set; }
    public BlobLease? Lease { get; set; }
}
