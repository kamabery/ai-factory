namespace Factory.Server.Abstractions;

/// <summary>Whether a PID read from a lock file (runner.lock, canary.lock, turn.lock, serve.lock) is a live process.</summary>
public interface IProcessProbe
{
    bool IsAlive(int pid);

    int CurrentPid { get; }
}
