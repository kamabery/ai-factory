using Factory.Server.Abstractions;
using Factory.Store.Abstractions;
using Microsoft.Extensions.Options;

namespace Factory.Server;

/// <summary>
/// Keeps the state files the dashboard polls fresh, at most once per FACTORY_STATE_POLL_SECONDS each: in azure from the
/// container, with sqlite and sql by bringing the exports up to the database (contract, "State backends").
/// </summary>
public sealed class StateCache(
    IStateSettings settings, IStateSync sync, IStateExport export, IOptions<ServerOptions> options, TimeProvider clock, ILogger<StateCache> log) : IStateCache
{
    private readonly Dictionary<string, DateTimeOffset> _seen = [];

    public void Board() => Throttled("board", () => { if (settings.IsDatabase) export.Export(); else sync.SyncBoard(); });

    public void Events() => Throttled("events", () => { if (settings.IsDatabase) export.Export(); else sync.SyncEvents(); });

    // A database backend's job files are on this machine already, unless they are shared (a deployed factory).
    public void Job(string id)
    {
        if (settings.SharesFiles) Throttled($"job-{id}", () => sync.Pull($"jobs/{id}/"));
    }

    private void Throttled(string key, Action run)
    {
        if (!settings.IsAzure && !settings.IsDatabase) return;
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
