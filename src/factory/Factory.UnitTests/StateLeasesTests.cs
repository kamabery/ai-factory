using Factory.Data.Abstractions;
using Factory.Store;
using Factory.Store.Abstractions;
using NSubstitute;

namespace Factory.UnitTests;

public class StateLeasesTests
{
    private readonly IStateSettings _settings = Substitute.For<IStateSettings>();
    private readonly IBlobClient _blobs = Substitute.For<IBlobClient>();
    private readonly ILeaseRows _rows = Substitute.For<ILeaseRows>();

    private StateLeases Leases() => new(_settings, _blobs, _rows);

    [Fact]
    public void Local_and_sqlite_are_one_machine_s_and_have_no_leases()
    {
        Assert.False(Leases().Shared);
        _settings.IsDatabase.Returns(true);
        Assert.False(Leases().Shared);
    }

    [Fact]
    public void Azure_leases_the_container_s_lock_blob()
    {
        _settings.IsAzure.Returns(true);
        _blobs.AcquireLease("runner", 0).Returns("b-1");
        _blobs.RenewLease("runner", "b-1").Returns(true);
        var l = Leases();

        Assert.True(l.Shared);
        Assert.Equal("b-1", l.Acquire("runner", 0));
        Assert.True(l.Renew("runner", "b-1"));
        l.Release("runner", "b-1");

        _blobs.Received(1).ReleaseLease("runner", "b-1");
        Assert.Empty(_rows.ReceivedCalls());
    }

    [Fact]
    public void Sql_leases_a_row_of_the_database()
    {
        _settings.IsDatabase.Returns(true);
        _settings.IsSql.Returns(true);
        _rows.Acquire("canary", 0).Returns("r-1");
        _rows.Renew("canary", "r-1").Returns(true);
        var l = Leases();

        Assert.True(l.Shared);
        Assert.Equal("r-1", l.Acquire("canary", 0));
        Assert.True(l.Renew("canary", "r-1"));
        l.Release("canary", "r-1");

        _rows.Received(1).Release("canary", "r-1");
        Assert.Empty(_blobs.ReceivedCalls());
    }
}
