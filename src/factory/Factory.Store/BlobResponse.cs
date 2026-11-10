namespace Factory.Store;

/// <summary>One Blob service answer: its status, headers (names without case) and body.</summary>
internal sealed record BlobResponse(int Status, Dictionary<string, string> Headers, byte[] Bytes)
{
    public string? Header(string name) => Headers.TryGetValue(name, out var v) ? v : null;
}
