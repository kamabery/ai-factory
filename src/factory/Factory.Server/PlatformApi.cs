using Factory.Server.Abstractions;
using Factory.Store.Abstractions;

namespace Factory.Server;

/// <summary>
/// The platform chip's endpoints (contract, "Dashboard API", spec 24): <c>platform status</c> cached for 30 s, a detached
/// <c>platform start</c> and <c>platform sleep</c>. Thin wrappers: the guard is the CLI's. A refusal is not cached.
/// </summary>
public sealed class PlatformApi(IFactoryCalls factory, IFactoryLauncher launcher, TimeProvider clock)
{
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(30);
    private readonly Lock _gate = new();
    private (string Status, DateTimeOffset At)? _last;

    public Reply Read()
    {
        lock (_gate)
        {
            var now = clock.GetUtcNow();
            if (_last is { } c && now - c.At < Ttl) return Reply.Of(CallResult.Done(c.Status));
            var r = factory.Run("platform", "status");
            if (!r.Ok) return Reply.Of(r);
            _last = (r.Message.Trim(), now);
            return Reply.Of(CallResult.Done(_last.Value.Status));
        }
    }

    /// <summary>Start: a few minutes until Ready, so detached; the chip follows the status.</summary>
    public Reply Start()
    {
        Drop();
        launcher.StartDetached(["platform", "start"]);
        return Reply.Of(CallResult.Done("starting the platform"));
    }

    public Reply Sleep()
    {
        var r = factory.Run("platform", "sleep");
        Drop();
        return Reply.Of(r);
    }

    private void Drop() { lock (_gate) _last = null; }
}
