namespace Factory.Store.Abstractions;

/// <summary>Starts the runner (contract, "Cloud mode (deployed)", Starting the runner): a local process, or one runner job execution.</summary>
public interface IRunnerStarter
{
    /// <summary>True when the runner is a Container Apps job, so it runs in another container, not as a process here.</summary>
    bool StartsExecutions { get; }

    /// <summary>Starts it; the runner.log line saying what started (<c>started runner</c>, <c>started runner execution &lt;name&gt;</c>).</summary>
    string Start();
}
