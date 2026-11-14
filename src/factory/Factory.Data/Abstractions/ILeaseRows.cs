namespace Factory.Data.Abstractions;

/// <summary>
/// The <c>Leases</c> table: a lock that holds across machines in <c>sql</c>, as the blob lease does in azure (contract,
/// "State backends", Database, Locks). A lease lasts 60 s from its last take or renewal.
/// </summary>
public interface ILeaseRows
{
    /// <summary>Takes the lease, waiting up to <paramref name="seconds"/> for a held one to lapse; its id, or null.</summary>
    string? Acquire(string name, int seconds);

    /// <summary>Extends the lease another 60 s; false when it is no longer this holder's.</summary>
    bool Renew(string name, string id);

    void Release(string name, string id);
}
