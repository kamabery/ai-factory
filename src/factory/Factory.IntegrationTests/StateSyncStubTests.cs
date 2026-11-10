using System.Text;
using System.Text.Json.Nodes;
using Factory.Core;
using Factory.Store.Abstractions;

namespace Factory.IntegrationTests;

/// <summary>Two homes on one container of blob-stub.ps1: push, pull, the board's ETag, events and the cap.</summary>
[Collection(BlobStubCollection.Name)]
public sealed class StateSyncStubTests(BlobStub stub) : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly string _container = stub.NewContainer();

    public void Dispose() => _dir.Dispose();

    private AzureHome NewHome(long? cap = null) => new(_dir[Guid.NewGuid().ToString("N")[..8]], _container, cap: cap);

    [Fact]
    public void What_one_home_pushes_the_other_pulls_and_a_file_deleted_locally_is_deleted_from_the_container()
    {
        var a = NewHome();
        var b = NewHome();
        a.Put("ideas/I-001/idea.md", "idea");
        a.Put("jobs/J-001/spec.md", "spec");
        a.Put("jobs/J-001/x.tmp", "temp");

        Assert.Equal(2, a.Sync.Push());
        Assert.Equal(0, a.Sync.Push());
        Assert.Equal(2, b.Sync.Pull());
        Assert.Equal("idea", File.ReadAllText(b.Path("ideas/I-001/idea.md")));

        File.Delete(a.Path("jobs/J-001/spec.md"));
        a.Sync.Push();
        Assert.False(stub.Blobs(_container).ContainsKey("jobs/J-001/spec.md"));
        Assert.True(File.Exists(b.Path("jobs/J-001/spec.md")));   // a pull never deletes
    }

    [Fact]
    public void A_second_home_sees_the_board_change_and_its_stale_write_is_refused()
    {
        var a = NewHome();
        var b = NewHome();
        Assert.Equal(BoardSync.None, a.Sync.SyncBoard());
        a.Sync.SaveBoard(Encoding.UTF8.GetBytes("{\"v\":1}"));

        Assert.Equal(BoardSync.Changed, b.Sync.SyncBoard());
        Assert.Equal(BoardSync.Same, b.Sync.SyncBoard());
        a.Sync.SaveBoard(Encoding.UTF8.GetBytes("{\"v\":2}"));

        var e = Assert.Throws<FactoryException>(() => b.Sync.SaveBoard(Encoding.UTF8.GetBytes("{\"v\":3}")));
        Assert.Equal("state conflict: board.json changed in Azure since it was read", e.Message);
        Assert.Equal("{\"v\":2}", stub.Blobs(_container)["board.json"]!["text"]!.GetValue<string>());
    }

    [Fact]
    public void Events_append_to_the_months_blob()
    {
        var a = NewHome();

        a.Sync.AppendEvent("{\"kind\":\"note\"}");
        a.Sync.AppendEvent("{\"kind\":\"idea\"}");

        var month = $"events/{DateTime.UtcNow:yyyy-MM}.jsonl";
        Assert.Equal("{\"kind\":\"note\"}\n{\"kind\":\"idea\"}\n", stub.Blobs(_container)[month]!["text"]!.GetValue<string>());
    }

    [Fact]
    public void The_cap_rolls_off_a_finished_jobs_logs_keeps_a_live_job_and_never_repushes_what_it_pruned()
    {
        var a = NewHome(cap: 30_000);
        a.Put("jobs/J-001/run-1.jsonl", new string('x', 20_000));
        a.Put("jobs/J-002/run-1.jsonl", new string('y', 20_000));
        a.Put("evals/J-001.md", "eval");
        a.Sync.Push();
        var jobs = new[]
        {
            new Job(new JsonObject { ["id"] = "J-001", ["stage"] = "done", ["updatedAt"] = "2026-01-01T00:00:00.000Z" }),
            new Job(new JsonObject { ["id"] = "J-002", ["stage"] = "build", ["updatedAt"] = "2026-01-02T00:00:00.000Z" }),
        };

        var r = a.Sync.Prune(jobs);

        Assert.Equal((1, 20_000L), (r.Deleted, r.Freed));
        var blobs = stub.Blobs(_container);
        Assert.False(blobs.ContainsKey("jobs/J-001/run-1.jsonl"));
        Assert.True(blobs.ContainsKey("jobs/J-002/run-1.jsonl") && blobs.ContainsKey("evals/J-001.md"));
        Assert.True(File.Exists(a.Path("jobs/J-001/run-1.jsonl")));
        File.AppendAllText(a.Path("jobs/J-001/run-1.jsonl"), "more");
        Assert.Equal(0, a.Sync.Push());
    }
}
