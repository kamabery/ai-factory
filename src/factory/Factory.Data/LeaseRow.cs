namespace Factory.Data;

/// <summary>A lock's lease in <c>sql</c> (runner, canary, turn-&lt;S&gt;): who holds it, and when it lapses.</summary>
public sealed class LeaseRow
{
    public string TenantId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Holder { get; set; } = "";

    /// <summary>Unix milliseconds, UTC: SQLite has no DateTimeOffset it can compare.</summary>
    public long ExpiresAt { get; set; }
}
