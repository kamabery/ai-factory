using System.Text.Json.Nodes;
using Factory.Cli;

namespace Factory.IntegrationTests;

/// <summary>
/// <c>state-import -Azure</c> end to end: a board an azure home wrote to the blob stub moves into a fresh sqlite home,
/// with its events and files, and the container is left exactly as it was.
/// </summary>
[Collection(BlobStubCollection.Name)]
public sealed class CliStateImportAzureTests(BlobStub stub) : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly string _container = stub.NewContainer();

    public void Dispose() => _dir.Dispose();

    private (int Code, string Out, string Err) Run(params string[] args)
    {
        var env = new Dictionary<string, string?>
        {
            ["FACTORY_HOME"] = _dir["sqlite"], ["FACTORY_STATE"] = "sqlite", ["FACTORY_BLOB_URL"] = _container,
            ["FACTORY_STATE_TOKEN"] = BlobStub.Token, ["FACTORY_ENV_FILE"] = _dir["none.env"], ["FACTORY_LOCAL_DIR"] = _dir["local"],
        };
        var stdout = new StringWriter { NewLine = "\n" };
        var stderr = new StringWriter { NewLine = "\n" };
        var code = CliHost.Run(args, env, _dir.Path, stdout, stderr);
        return (code, stdout.ToString(), stderr.ToString());
    }

    [Fact]
    public void Moves_the_container_s_board_events_and_files_into_the_database_and_writes_nothing_to_the_container()
    {
        var azure = new AzureHome(_dir["azure"], _container);
        using (var s = azure.Store.Open())
        {
            s.Board["seq"] = new JsonObject { ["I"] = 1, ["J"] = 1 };
            s.Board["ideas"]!.AsArray().Add(new JsonObject { ["id"] = "I-001", ["status"] = "drafting", ["text"] = "shared" });
            s.Board["jobs"]!.AsArray().Add(new JsonObject { ["id"] = "J-001", ["idea"] = "I-001", ["stage"] = "ready", ["round"] = 2, ["title"] = "Moved" });
            s.Save();
            s.AddEvent(new JsonObject { ["ts"] = "2026-10-31T23:00:00Z", ["idea"] = "I-001", ["kind"] = "idea", ["detail"] = "drafting" });
            s.AddEvent(new JsonObject { ["ts"] = "2026-11-01T01:00:00Z", ["job"] = "J-001", ["kind"] = "note", ["detail"] = "two months" });
            azure.Put("jobs/J-001/spec.md", "# Moved");
            s.Close();
        }
        // ETags change on every write, even of the same bytes.
        string Listing() => string.Join("\n", azure.Blobs.List("", versions: true).Select(b => $"{b.Name} {b.VersionId} {b.ETag} {b.Leased}"));
        var before = Listing();

        var r = Run("state-import", "-Azure");

        Assert.Equal((0, "imported 1 ideas, 1 jobs, 2 events\ncheck: board.json and events.jsonl match the database\n", ""), r);
        Assert.Equal(before, Listing());
        Assert.Equal("# Moved", File.ReadAllText(Path.Combine(_dir["sqlite"], "jobs", "J-001", "spec.md")));
        Assert.Equal((0, "I-001  idea:drafting shared\nJ-001  ready      r2 Moved\n", ""), Run("status"));
        Assert.Equal(File.ReadAllText(azure.Path("board.json")), File.ReadAllText(Path.Combine(_dir["sqlite"], "board.json")));
        Assert.Equal(2, File.ReadAllLines(Path.Combine(_dir["sqlite"], "events.jsonl")).Length);
    }

    [Fact]
    public void An_empty_container_is_refused_and_the_database_stays_empty()
    {
        Assert.Equal((1, "", "factory: state-import refused: the container has no board.json\n"), Run("state-import", "-Azure"));
        Assert.Equal((0, "board is empty\n", ""), Run("status"));
    }
}
