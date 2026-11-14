using Factory.Server.Abstractions;
using Microsoft.Extensions.Options;

namespace Factory.Server;

/// <summary>
/// Stops the server when serve.ps1, which forwarded to it (--parent-pid), is gone, so killing serve.ps1 alone never
/// leaves an orphaned server holding serve.lock, the port and the watchdog. The parent is the PID and the start time
/// it had when the watch began, so a reused PID does not count. Started directly (no --parent-pid), it does nothing.
/// </summary>
public sealed class ParentWatch(IOptions<ServerOptions> options, IProcessProbe procs, IHostApplicationLifetime lifetime, TimeProvider clock,
    ILogger<ParentWatch> log) : BackgroundService
{
    private bool _begun;
    private DateTime? _started;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (options.Value.ParentPid <= 0) return;
        while (!stoppingToken.IsCancellationRequested && !Check())
        {
            try { await Task.Delay(TimeSpan.FromSeconds(1), clock, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>One look at the parent; true when it is gone and the host was told to stop.</summary>
    public bool Check()
    {
        var pid = options.Value.ParentPid;
        if (pid <= 0) return false;
        var now = procs.StartTime(pid);
        if (!_begun) { _begun = true; _started = now; }
        if (_started is not null && now == _started) return false;
        log.LogWarning("the dashboard's parent (PID {Pid}) is gone: stopping the dashboard", pid);
        lifetime.StopApplication();
        return true;
    }
}
