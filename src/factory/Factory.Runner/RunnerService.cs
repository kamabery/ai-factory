using Microsoft.Extensions.Hosting;

namespace Factory.Runner;

/// <summary>Runs one drain, then stops the host: the runner is started per approval and exits when the queue is empty.</summary>
public sealed class RunnerService(RunnerLoop loop, IHostApplicationLifetime lifetime) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.Run(() =>
    {
        try { loop.Run(); }
        finally { lifetime.StopApplication(); }
    }, CancellationToken.None);
}
