using Factory.Store.Abstractions;

namespace Factory.Cli.Abstractions;

/// <summary>
/// canary.ps1 (contract, "Canary"): a fresh scaffold of the current templates, deployed end to end as j000 and torn down,
/// so a broken deploy path costs a canary rather than a real job's attempts.
/// </summary>
public interface ICanaryService
{
    /// <summary>
    /// <c>canary [-Force] [-Full]</c>: up to date, or one run under canary.lock, recorded in canary.json and canary-&lt;n&gt;.log,
    /// then <c>canary-destroy</c> started detached. Returns its output line; a failed canary refuses with its first error.
    /// </summary>
    string Run(IBoardSession session, bool force, bool full);

    /// <summary><c>canary-destroy</c>: j000 down, pass or fail, and the outcome under <c>teardown</c>; prints its status.</summary>
    IReadOnlyList<string> Teardown(IBoardSession session);

    /// <summary><c>canary -Retry</c>: a recorded failure marked <c>retry</c>, so the runner runs the canary again; runs nothing.</summary>
    string Retry();
}
