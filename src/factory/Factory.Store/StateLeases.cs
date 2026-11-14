using Factory.Data.Abstractions;
using Factory.Store.Abstractions;

namespace Factory.Store;

/// <summary>The backend's leases: the container's in azure, the database's in sql, none in local or sqlite.</summary>
public sealed class StateLeases(IStateSettings settings, IBlobClient blobs, ILeaseRows rows) : IStateLeases
{
    public bool Shared => settings.IsAzure || settings.IsSql;

    public string? Acquire(string name, int seconds) => settings.IsAzure ? blobs.AcquireLease(name, seconds) : rows.Acquire(name, seconds);

    public bool Renew(string name, string id) => settings.IsAzure ? blobs.RenewLease(name, id) : rows.Renew(name, id);

    public void Release(string name, string id)
    {
        if (settings.IsAzure) blobs.ReleaseLease(name, id);
        else rows.Release(name, id);
    }
}
