namespace Factory.TestHost;

/// <summary>A lease on a stub blob: its id, when it lapses (MaxValue for an infinite one), and the duration it renews for.</summary>
public sealed class BlobLease
{
    public required string Id { get; init; }
    public required DateTime Until { get; set; }
    public required int Duration { get; init; }
}
