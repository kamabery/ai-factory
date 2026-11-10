using Factory.Cli;

namespace Factory.IntegrationTests;

/// <summary><c>status</c> on the azure backend, end to end in process: real composition, blob-stub.ps1, exact output.</summary>
[Collection(BlobStubCollection.Name)]
public sealed class CliAzureStatusTests(BlobStub stub) : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly string _container = stub.NewContainer();

    public void Dispose() => _dir.Dispose();

    private Dictionary<string, string?> Env(string home, params (string, string)[] more)
    {
        var env = new Dictionary<string, string?>
        {
            ["FACTORY_HOME"] = home, ["FACTORY_STATE"] = "azure", ["FACTORY_BLOB_URL"] = _container,
            ["FACTORY_STATE_TOKEN"] = BlobStub.Token, ["FACTORY_ENV_FILE"] = _dir["none.env"], ["FACTORY_LOCAL_DIR"] = _dir["local"],
        };
        foreach (var (k, v) in more) env[k] = v;
        return env;
    }

    private (int Code, string Out, string Err) Run(Dictionary<string, string?> env, params string[] args)
    {
        var stdout = new StringWriter { NewLine = "\n" };
        var stderr = new StringWriter { NewLine = "\n" };
        var code = CliHost.Run(args, env, _dir.Path, stdout, stderr);
        return (code, stdout.ToString(), stderr.ToString());
    }

    [Fact]
    public void An_empty_container_is_an_empty_board_and_the_lease_is_free_after()
    {
        Assert.Equal((0, "board is empty\n", ""), Run(Env(_dir["a"]), "status"));

        Assert.False(stub.Blobs(_container)["locks/board"]!["leased"]!.GetValue<bool>());
    }

    [Fact]
    public void A_second_home_reads_the_board_another_wrote_and_pulls_the_rest()
    {
        var a = new AzureHome(_dir["a"], _container);
        using (var s = a.Store.Open())
        {
            s.Board["ideas"]!.AsArray().Add(new System.Text.Json.Nodes.JsonObject { ["id"] = "I-001", ["status"] = "drafting", ["text"] = "shared" });
            s.Save();
            a.Put("ideas/I-001/idea.md", "shared idea");
            s.Close();
        }

        Assert.Equal((0, "I-001  idea:drafting shared\n", ""), Run(Env(_dir["b"]), "status"));
        Assert.Equal("shared idea", File.ReadAllText(Path.Combine(_dir["b"], "ideas", "I-001", "idea.md")));
    }

    [Fact]
    public void A_local_board_the_container_lacks_needs_state_migrate()
    {
        Directory.CreateDirectory(_dir["a"]);
        File.WriteAllText(Path.Combine(_dir["a"], "board.json"), "{\"version\":2}");

        var r = Run(Env(_dir["a"]), "status");

        Assert.Equal((1, "", $"factory: state: the container has no board.json but {Path.Combine(_dir["a"], "board.json")} exists; run factory.ps1 state-migrate\n"), r);
        Assert.False(stub.Blobs(_container)["locks/board"]!["leased"]!.GetValue<bool>());
    }

    [Fact]
    public void Refuses_while_another_machine_holds_the_board_lease()
    {
        var other = new AzureHome(_dir["b"], _container);
        var held = other.Blobs.AcquireLease("board", 5)!;
        try
        {
            var r = Run(Env(_dir["a"], ("FACTORY_STATE_LOCK_SECONDS", "1")), "status");

            Assert.Equal((1, "", "factory: state lock refused: another machine has held the board (locks/board) for over 1 s\n"), r);
        }
        finally { other.Blobs.ReleaseLease("board", held); }
    }
}
