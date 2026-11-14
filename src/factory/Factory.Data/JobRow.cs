namespace Factory.Data;

/// <summary>One job on the board: its JSON object is the source, the other columns copies for queries.</summary>
public sealed class JobRow
{
    public string TenantId { get; set; } = "";
    public string Id { get; set; } = "";

    /// <summary>Its place in board.json's <c>jobs</c>.</summary>
    public long Ordinal { get; set; }

    public string? Idea { get; set; }
    public string? Stage { get; set; }
    public int Round { get; set; }
    public string? Target { get; set; }
    public string? Kind { get; set; }
    public string Json { get; set; } = "";
    public long Version { get; set; }
}
