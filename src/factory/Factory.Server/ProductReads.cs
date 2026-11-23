using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Factory.Server.Abstractions;

namespace Factory.Server;

/// <summary>
/// GET /api/ideas/&lt;I&gt;/product: <c>factory product</c> for a repo card, cached for 60 s per idea (it reads GitHub), and
/// dropped when Implement, a feature's Confirm or Dismiss changes what it says (contract, "Dashboard API").
/// </summary>
public sealed class ProductReads(IFactoryCalls factory, TimeProvider clock)
{
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(60);
    private readonly ConcurrentDictionary<string, (string Json, DateTimeOffset At)> _last = new(StringComparer.OrdinalIgnoreCase);

    public Reply Read(string idea)
    {
        var now = clock.GetUtcNow();
        if (_last.TryGetValue(idea, out var c) && now - c.At < Ttl) return Reply.Raw(c.Json);
        var r = factory.Run("product", idea);
        if (!r.Ok) { _last.TryRemove(idea, out _); return Reply.Of(r); }
        _last[idea] = (r.Message.Trim(), now);
        return Reply.Raw(r.Message.Trim());
    }

    /// <summary>The product as JSON, or null with the refusal.</summary>
    public (JsonObject? Product, string? Error) Get(string idea)
    {
        var r = Read(idea);
        if (r.Status != 200) return (null, JsonNode.Parse(r.Body)?["message"]?.GetValue<string>());
        return (JsonNode.Parse(r.Body) as JsonObject, null);
    }

    public void Drop(string idea) => _last.TryRemove(idea, out _);
}
