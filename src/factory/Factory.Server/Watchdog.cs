using Factory.Core.Abstractions;
using Factory.Server.Abstractions;
using Factory.Store.Abstractions;
using Microsoft.Extensions.Options;

namespace Factory.Server;

/// <summary>
/// serve.ps1's Invoke-Watchdog and Invoke-PrSync. The watchdog starts the runner when Approved is not empty and no live
/// runner holds runner.lock (one that died, or an approval made outside the dashboard); a lock younger than 5 s may not
/// hold its PID yet. PR sync starts factory.ps1 sync-prs, detached, while a ready job has a PR the human may have merged.
/// </summary>
public sealed class Watchdog(
    BoardFile board, IFactoryPaths paths, IFileStore files, IFactoryLauncher launcher, IProcessProbe procs, IOptions<ServerOptions> options,
    TimeProvider clock, ILogger<Watchdog> log) : BackgroundService
{
    private DateTimeOffset _nextWatch = DateTimeOffset.MinValue;
    private DateTimeOffset _nextSync = DateTimeOffset.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            Tick();
            try { await Task.Delay(TimeSpan.FromMilliseconds(250), clock, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    public void Tick()
    {
        var now = clock.GetUtcNow();
        if (now >= _nextWatch)
        {
            _nextWatch = now.AddSeconds(options.Value.WatchdogSeconds);
            try { Watch(now); } catch (Exception e) { log.LogWarning("watchdog: {Message}", e.Message); }
        }
        if (now >= _nextSync)
        {
            _nextSync = now.AddSeconds(options.Value.PrSyncSeconds);
            try
            {
                if (board.Jobs().Any(j => BoardFile.Str(j, "stage") == "ready" && IdeaSessions.Truthy(j["pr"]))) launcher.StartDetached(["sync-prs"]);
            }
            catch (Exception e) { log.LogWarning("pr sync: {Message}", e.Message); }
        }
    }

    private void Watch(DateTimeOffset now)
    {
        if (!board.Jobs().Any(j => BoardFile.Str(j, "stage") == "approved")) return;
        var lockPath = Path.Combine(paths.Home, "runner.lock");
        if (files.Stat(lockPath) is { } stat)
        {
            if (new DateTimeOffset(stat.LastWriteTicks, TimeSpan.Zero) > now.AddSeconds(-5)) return;
            if (ServerFiles.LockHolder(files, lockPath) is { } pid && procs.IsAlive(pid)) return;
        }
        launcher.StartScript("runner.ps1", []);
        files.AppendText(Path.Combine(paths.Home, "runner.log"), $"{now.UtcDateTime:o} watchdog: started runner{Environment.NewLine}");
    }
}
