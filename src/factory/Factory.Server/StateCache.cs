using Factory.Server.Abstractions;
using Factory.Store.Abstractions;
using Microsoft.Extensions.Options;

namespace Factory.Server;

public sealed class StateCache(IStateSettings settings, IStateSync sync, IOptions<ServerOptions> options, TimeProvider clock, ILogger<StateCache> log) : IStateCache
{
    private readonly Dictionary<string, DateTimeOffset> _seen = [];

    public void Board() => Throttled("board", () => sync.SyncBoard());

    public void Events() => Throttled("events", () => sync.SyncEvents());

    public void Job(string id) => Throttled($"job-{id}", () => sync.Pull($"jobs/{id}/"));

    private void Throttled(string key, Action run)
    {
        if (!settings.IsAzure) return;
        var now = clock.GetUtcNow();
        lock (_seen)
        {
            if (_seen.TryGetValue(key, out var at) && (now - at).TotalSeconds < options.Value.StatePollSeconds) return;
            _seen[key] = now;
        }
        try { run(); }
        catch (Exception e) { log.LogWarning("state sync {Key}: {Message}", key, e.Message); }
    }
}
