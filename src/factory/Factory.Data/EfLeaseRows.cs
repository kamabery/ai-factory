using Factory.Data.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace Factory.Data;

/// <summary>The <c>Leases</c> table (contract, "State backends", Database, Locks): 60 s leases, taken, renewed and released by holder id.</summary>
public sealed class EfLeaseRows(FactoryDbs dbs, TimeProvider clock) : ILeaseRows
{
    private const long LeaseMs = 60_000;

    private string T => dbs.TenantId;

    private long Now => clock.GetUtcNow().ToUnixTimeMilliseconds();

    public string? Acquire(string name, int seconds) => DbErrors.Guard(() => AcquireRow(name, seconds));

    private string? AcquireRow(string name, int seconds)
    {
        var until = clock.GetUtcNow().AddSeconds(seconds);
        while (true)
        {
            if (TryTake(name) is { } id) return id;
            if (clock.GetUtcNow() >= until) return null;
            Thread.Sleep(500);
        }
    }

    private string? TryTake(string name)
    {
        using var db = dbs.Open();
        using var tx = db.Database.BeginTransaction();
        var id = Guid.NewGuid().ToString("N");
        var now = Now;
        var row = db.Leases.AsNoTracking().FirstOrDefault(x => x.TenantId == T && x.Name == name);
        if (row is null)
        {
            db.Leases.Add(new LeaseRow { TenantId = T, Name = name, Holder = id, ExpiresAt = now + LeaseMs });
            try { db.SaveChanges(); }
            catch (DbUpdateException) { return null; }   // another machine took it first
        }
        else if (row.ExpiresAt > now) return null;
        else if (db.Leases.Where(x => x.TenantId == T && x.Name == name && x.Holder == row.Holder && x.ExpiresAt == row.ExpiresAt)
            .ExecuteUpdate(s => s.SetProperty(x => x.Holder, id).SetProperty(x => x.ExpiresAt, now + LeaseMs)) == 0) return null;
        tx.Commit();
        return id;
    }

    public bool Renew(string name, string id) => DbErrors.Guard(() =>
    {
        using var db = dbs.Open();
        var until = Now + LeaseMs;
        return db.Leases.Where(x => x.TenantId == T && x.Name == name && x.Holder == id).ExecuteUpdate(s => s.SetProperty(x => x.ExpiresAt, until)) == 1;
    });

    public void Release(string name, string id) => DbErrors.Guard(() =>
    {
        using var db = dbs.Open();
        db.Leases.Where(x => x.TenantId == T && x.Name == name && x.Holder == id).ExecuteDelete();
    });
}
