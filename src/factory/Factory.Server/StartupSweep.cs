using Factory.Store.Abstractions;

namespace Factory.Server;

/// <summary>
/// The dashboard's startup <c>factory sweep</c> (contract, "Deploy"): previews that outlived their work, in case nothing
/// has drained the runner since. It runs once the server is listening, so a start that fails to bind sweeps nothing.
/// </summary>
public sealed class StartupSweep(IFactoryLauncher launcher) : IHostedLifecycleService
{
    public Task StartedAsync(CancellationToken cancellationToken)
    {
        launcher.StartDetached(["sweep"]);
        return Task.CompletedTask;
    }

    public Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
