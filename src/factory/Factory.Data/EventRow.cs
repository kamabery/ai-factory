namespace Factory.Data;

/// <summary>One events.jsonl line exactly as written, in <see cref="Seq"/> order, with copies of its keys for queries.</summary>
public sealed class EventRow
{
    public long Seq { get; set; }
    public string TenantId { get; set; } = "";
    public string? Ts { get; set; }
    public string? Job { get; set; }
    public string? Idea { get; set; }
    public string? Kind { get; set; }
    public string Line { get; set; } = "";
}
