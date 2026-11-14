namespace Factory.Store.Abstractions;

/// <summary>
/// The leases that make a lock hold across machines (contract, "State backends"): the blob lease on locks/&lt;name&gt; in
/// azure, a row in the Leases table in sql. local and sqlite are one machine's, and have none.
/// </summary>
public interface IStateLeases
{
    /// <summary>Whether this backend has leases at all.</summary>
    bool Shared { get; }

    /// <summary>Takes the lease, waiting up to <paramref name="seconds"/>; its id, or null while another holds it.</summary>
    string? Acquire(string name, int seconds);

    /// <summary>False when the lease is no longer this holder's.</summary>
    bool Renew(string name, string id);

    void Release(string name, string id);
}
