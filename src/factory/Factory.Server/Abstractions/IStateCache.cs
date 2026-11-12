namespace Factory.Server.Abstractions;

/// <summary>
/// serve.ps1's Sync-Throttled: in azure the dashboard's files are a cache of the container, refreshed at most every
/// FACTORY_STATE_POLL_SECONDS per thing. A failure is a warning, never a 500. Locally each call does nothing.
/// </summary>
public interface IStateCache
{
    void Board();

    void Events();

    /// <summary>state-pull jobs/&lt;id&gt;/ when a job is opened.</summary>
    void Job(string id);
}
