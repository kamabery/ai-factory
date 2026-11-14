namespace Factory.Data;

/// <summary>One idea on the board: its JSON object is the source, the other columns copies for queries.</summary>
public sealed class IdeaRow
{
    public string TenantId { get; set; } = "";
    public string Id { get; set; } = "";

    /// <summary>Its place in board.json's <c>ideas</c>.</summary>
    public long Ordinal { get; set; }

    public string? Status { get; set; }
    public string? Target { get; set; }
    public string Json { get; set; } = "";
    public long Version { get; set; }
}
