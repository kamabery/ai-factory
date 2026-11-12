namespace Factory.Store.Abstractions;

/// <summary>
/// The one-runner guarantee (contract, "Runner" and "State backends"): <c>runner.lock</c> holding the runner's PID, and
/// in azure the lease on <c>locks/runner</c>, renewed while it is held, so one runner drains the queue across machines.
/// </summary>
public interface IRunnerLock
{
    /// <summary>
    /// Takes the lock, or null while a live process holds it (a lock whose PID is gone is taken over) or, in azure,
    /// another machine holds the lease. Dispose releases both.
    /// </summary>
    IDisposable? TryTake();
}
