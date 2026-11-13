using Factory.Store.Abstractions;
using Microsoft.Extensions.Options;

namespace Factory.Store;

/// <summary>
/// runner.ps1's Take-Lock and its azure lease: <c>runner.lock</c> holding this process's PID (serve.ps1's watchdog reads
/// it), and in azure the lease on <c>locks/runner</c>. See <see cref="LeasedLock"/>.
/// </summary>
public sealed class RunnerLock(IFactoryPaths paths, IStateSettings settings, IBlobClient blobs, IOptions<StateOptions> options) : IRunnerLock
{
    public IDisposable? TryTake() => new LeasedLock(Path.Combine(paths.Home, "runner.lock"), "runner", settings, blobs, options).TryTake();
}
