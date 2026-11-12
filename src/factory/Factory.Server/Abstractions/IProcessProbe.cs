namespace Factory.Server.Abstractions;

/// <summary>Whether a PID read from a lock file (runner.lock, canary.lock, turn.lock, serve.lock) is a live process.</summary>
public interface IProcessProbe
{
    bool IsAlive(int pid);

    /// <summary>When the process started (UTC), or null when no such process runs. Tells a reused PID from the original.</summary>
    DateTime? StartTime(int pid);

    int CurrentPid { get; }
}
