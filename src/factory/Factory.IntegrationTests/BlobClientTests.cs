using System.Text;
using Factory.Core;

namespace Factory.IntegrationTests;

/// <summary>BlobClient against blob-stub.ps1: the REST calls, ETags, paging, append blobs and leases.</summary>
[Collection(BlobStubCollection.Name)]
public sealed class BlobClientTests(BlobStub stub) : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private AzureHome NewHome(string? container = null, string token = BlobStub.Token) =>
        new(_dir[Guid.NewGuid().ToString("N")[..8]], container ?? stub.NewContainer(), token);

    private static byte[] B(string s) => Encoding.UTF8.GetBytes(s);
    private static string T(byte[]? b) => Encoding.UTF8.GetString(b!);

    [Fact]
    public void Writes_reads_answers_not_modified_for_its_etag_and_refuses_a_stale_if_match()
    {
        var blobs = NewHome().Blobs;

        var etag = blobs.Write("a/b c.txt", B("one"));
        var r = blobs.Read("a/b c.txt")!;
        Assert.Equal(("one", etag), (T(r.Bytes), r.ETag));
        Assert.True(blobs.Read("a/b c.txt", etag)!.NotModified);
        blobs.Write("a/b c.txt", B("two"), ifMatch: etag);

        var e = Assert.Throws<FactoryException>(() => blobs.Write("a/b c.txt", B("three"), ifMatch: etag));
        Assert.Equal("state conflict: a/b c.txt changed in Azure since it was read", e.Message);
        Assert.Equal("two", T(blobs.Read("a/b c.txt")!.Bytes));
        Assert.Null(blobs.Read("missing"));
        Assert.Throws<FactoryException>(() => blobs.Write("a/b c.txt", B("x"), ifNoneMatchAny: true));
    }

    [Fact]
    public void A_refused_token_is_an_error_naming_the_status_and_code()
    {
        var blobs = NewHome(token: "wrong").Blobs;

        Assert.Equal("state: GET x answered 403 AuthorizationPermissionMismatch", Assert.Throws<FactoryException>(() => blobs.Read("x")).Message);
    }

    [Fact]
    public void Lists_across_pages_with_versions_on_request()
    {
        var blobs = NewHome().Blobs;
        foreach (var i in new[] { 1, 2, 3 }) blobs.Write($"p/{i}", B($"{i}"));
        blobs.Write("p/1", B("1b"));
        blobs.Write("q/1", B("x"));

        Assert.Equal(["p/1", "p/2", "p/3"], blobs.List("p/").Select(b => b.Name));
        var all = blobs.List("p/", versions: true);
        Assert.Equal(4, all.Count);
        Assert.Single(all, b => b.Name == "p/1" && !b.IsCurrent);
        Assert.All(blobs.List("p/"), b => Assert.True(b.IsCurrent && b.Length > 0 && b.ETag is not null && !b.ETag.Contains('"')));
        Assert.Empty(blobs.List("nothing/"));
    }

    [Fact]
    public void Appends_blocks_creating_the_append_blob_first()
    {
        var blobs = NewHome().Blobs;

        var first = blobs.Append("events/2026-11.jsonl", B("a\n"));
        var second = blobs.Append("events/2026-11.jsonl", B("b\n"));

        Assert.Equal((0L, 2L), (first.Offset, second.Offset));
        Assert.Equal("a\nb\n", T(blobs.Read("events/2026-11.jsonl")!.Bytes));
    }

    [Fact]
    public void Deletes_a_blob_and_an_old_version_and_a_missing_one_is_fine()
    {
        var blobs = NewHome().Blobs;
        blobs.Write("d", B("1"));
        blobs.Write("d", B("2"));
        var old = blobs.List("d", versions: true).Single(b => !b.IsCurrent);

        blobs.Delete("d", old.VersionId);
        blobs.Delete("d");
        blobs.Delete("never");

        Assert.Single(blobs.List("d", versions: true));   // the deleted current blob, now a version
        Assert.Null(blobs.Read("d"));
    }

    [Fact]
    public void Says_which_role_is_missing_when_azure_refuses_to_delete_an_old_version()
    {
        var blobs = NewHome().Blobs;
        blobs.Write("v", B("1"));
        blobs.Write("v", B("2"));
        var old = blobs.List("v", versions: true).Single(b => !b.IsCurrent);
        stub.DenyVersionDelete(true);
        try
        {
            var e = Assert.Throws<FactoryException>(() => blobs.Delete("v", old.VersionId));
            Assert.Equal("state: DELETE v answered 403 OperationNotAllowedOnAutomaticSnapshot (deleting an old version needs Storage Blob Data Owner on the container, not Contributor: apply factory/infra/state)", e.Message);
        }
        finally { stub.DenyVersionDelete(false); }
    }

    [Fact]
    public void One_lease_holder_at_a_time_and_a_released_lease_can_be_taken_again()
    {
        var container = stub.NewContainer();
        var a = NewHome(container).Blobs;
        var b = NewHome(container).Blobs;

        var id = a.AcquireLease("board", 0)!;
        Assert.Null(b.AcquireLease("board", 1));
        Assert.True(stub.Blobs(container)["locks/board"]!["leased"]!.GetValue<bool>());
        a.ReleaseLease("board", id);

        Assert.NotNull(b.AcquireLease("board", 0));
    }

    [Fact]
    public void A_lapsed_lease_is_taken_over_and_its_old_holder_can_no_longer_renew_it()
    {
        var container = stub.NewContainer();
        var a = NewHome(container).Blobs;
        var b = NewHome(container).Blobs;
        var old = a.AcquireLease("runner", 0)!;
        Assert.True(a.RenewLease("runner", old));

        var taken = b.AcquireLease("runner", 10);   // waits out the 3 s lease

        Assert.NotNull(taken);
        Assert.False(a.RenewLease("runner", old));
        a.ReleaseLease("runner", old);               // not ours any more: a no-op, as factory.ps1's
        Assert.True(b.RenewLease("runner", taken));
    }
}
