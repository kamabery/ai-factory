using Factory.Store;
using Microsoft.Extensions.Options;

namespace Factory.IntegrationTests;

/// <summary>
/// runner.lock, canary.lock and an idea turn's turn.lock in azure (contract, "State backends"): the file and the lease on
/// locks/&lt;name&gt; together, on the blob stub, whose leases run 20 times faster (60 s lasts 3 s).
/// </summary>
[Collection(BlobStubCollection.Name)]
public sealed class LeasedLockTests(BlobStub stub) : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly string _container = stub.NewContainer();

    public void Dispose() => _dir.Dispose();

    private (LeasedLock Lock, string File) Lock(AzureHome home, string renewSeconds = "20")
    {
        var file = Path.Combine(home.Home, "runner.lock");
        var options = Options.Create(new StateOptions { RenewSeconds = renewSeconds });
        return (new LeasedLock(file, "runner", home.Settings, home.Blobs, options), file);
    }

    private bool Leased() => stub.Blobs(_container)["locks/runner"]?["leased"]?.GetValue<bool>() == true;

    [Fact]
    public void In_azure_a_lease_held_on_another_machine_refuses_and_leaves_no_lock_file()
    {
        var other = new AzureHome(_dir["b"], _container);
        var held = other.Blobs.AcquireLease("runner", 5)!;
        try
        {
            var (l, file) = Lock(new AzureHome(_dir["a"], _container));

            Assert.Null(l.TryTake(out var leaseRefused));
            Assert.True(leaseRefused);
            Assert.False(File.Exists(file));
        }
        finally { other.Blobs.ReleaseLease("runner", held); }
    }

    [Fact]
    public void Releasing_frees_the_lease_for_the_next_taker()
    {
        var (a, file) = Lock(new AzureHome(_dir["a"], _container));
        var held = a.TryTake();
        Assert.NotNull(held);
        Assert.True(Leased());
        Assert.True(File.Exists(file));

        held!.Dispose();

        Assert.False(Leased());
        Assert.False(File.Exists(file));
        using var next = Lock(new AzureHome(_dir["b"], _container)).Lock.TryTake();
        Assert.NotNull(next);
    }

    [Fact]
    public async Task The_keeper_renews_the_lease_while_its_holder_works()
    {
        var (l, _) = Lock(new AzureHome(_dir["a"], _container), renewSeconds: "1");
        using var held = l.TryTake();
        Assert.NotNull(held);

        // Past the 3 s a lease lasts on the stub: only renewals keep it.
        await Task.Delay(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.True(Leased());
    }
}
