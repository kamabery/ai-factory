namespace Factory.Data;

/// <summary>The board's own keys (contract, "State backends", Database): one row per tenant.</summary>
public sealed class BoardRow
{
    public string TenantId { get; set; } = "";

    /// <summary>board.json without its ideas and jobs: those keys stay, in their places, as empty arrays.</summary>
    public string Skeleton { get; set; } = "";

    /// <summary>Bumped by every committed save and event, so an export knows when it is behind.</summary>
    public long Revision { get; set; }

    /// <summary>The optimistic concurrency check on the board's own keys.</summary>
    public long Version { get; set; }
}
