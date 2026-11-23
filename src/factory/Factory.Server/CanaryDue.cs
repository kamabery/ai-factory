using Factory.Server.Abstractions;

namespace Factory.Server;

/// <summary>GET /api/canary: <c>canary -Due</c> for the due chip, cached for 60 s (the fingerprint reads the HCP variable set).</summary>
public sealed class CanaryDue(IFactoryCalls factory, TimeProvider clock)
{
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(60);
    private readonly Lock _gate = new();
    private (string Json, DateTimeOffset At)? _last;

    public Reply Read()
    {
        lock (_gate)
        {
            var now = clock.GetUtcNow();
            if (_last is { } c && now - c.At < Ttl) return Reply.Raw(c.Json);
            var r = factory.Run("canary", "-Due");
            if (!r.Ok) return Reply.Of(r);
            _last = (r.Message.Trim(), now);
            return Reply.Raw(_last.Value.Json);
        }
    }
}
