namespace Factory.Store.Abstractions;

/// <summary>A GET's answer: the bytes and ETag, or not modified (no bytes).</summary>
public sealed record BlobRead(bool NotModified, string? ETag, byte[]? Bytes);
