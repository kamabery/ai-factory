namespace Factory.Store.Abstractions;

/// <summary>
/// One canary at a time (contract, "Canary"): <c>canary.lock</c> holding the PID of the process running it (one whose
/// process is gone is taken over), and in azure the lease on <c>locks/canary</c>, so one canary runs across machines.
/// </summary>
public interface ICanaryLock
{
    /// <summary>
    /// Takes the lock, or null while another holds it; <paramref name="otherMachine"/> is true when it was the lease that
    /// refused. Dispose releases both.
    /// </summary>
    IDisposable? TryTake(out bool otherMachine);
}
