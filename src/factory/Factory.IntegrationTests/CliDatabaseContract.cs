using Factory.Cli;

namespace Factory.IntegrationTests;

/// <summary>
/// The CLI end to end on a database backend (contract, "State backends", Database): real composition, real database,
/// board.json and events.jsonl as exports. Run on sqlite and on sql (SQL Server).
/// </summary>
public abstract class CliDatabaseContract : IDisposable
{
    protected readonly TempDir Dir = new();

    protected string Home => Dir["state"];

    public void Dispose() => Dir.Dispose();

    /// <summary>FACTORY_STATE and the database setting for this backend.</summary>
    protected abstract Dictionary<string, string?> Backend();

    protected (int Code, string Out, string Err) Run(params string[] args)
    {
        var env = new Dictionary<string, string?>(Backend()) { ["FACTORY_HOME"] = Home, ["FACTORY_ENV_FILE"] = Dir["none.env"] };
        var stdout = new StringWriter { NewLine = "\n" };
        var stderr = new StringWriter { NewLine = "\n" };
        var code = CliHost.Run(args, env, Dir.Path, stdout, stderr);
        return (code, stdout.ToString(), stderr.ToString());
    }

    private string File(string rel) => Path.Combine(Home, rel);

    private const string Board = """
        {
          "version": 2,
          "seq": { "I": 1, "J": 1 },
          "ideas": [ { "id": "I-001", "text": "find books", "status": "done", "jobs": [ "J-001" ] } ],
          "jobs": [ { "id": "J-001", "idea": "I-001", "title": "Search", "stage": "backlog", "round": 1, "flag": null } ]
        }
        """;

    private void Seed()
    {
        Directory.CreateDirectory(Path.Combine(Home, "jobs", "J-001"));
        System.IO.File.WriteAllText(File("board.json"), Board);
        System.IO.File.WriteAllText(File("events.jsonl"), "{\"ts\":\"2026-11-01T10:00:00Z\",\"idea\":\"I-001\",\"kind\":\"idea\",\"detail\":\"drafting\"}\n\n");
        System.IO.File.WriteAllText(Path.Combine(Home, "jobs", "J-001", "spec.md"), "# Search\n\n## Target\n- Repo: kamabery/books\n\n## Gate\n- build: `exit 0`\n");
    }

    [Fact]
    public void An_empty_database_is_an_empty_board_and_exports_nothing()
    {
        Assert.Equal((0, "board is empty\n", ""), Run("status"));

        Assert.False(System.IO.File.Exists(File("board.json")));
    }

    [Fact]
    public void A_board_json_beside_an_empty_database_refuses_until_it_is_imported()
    {
        Seed();

        Assert.Equal((1, "", $"factory: state: the database has no board but {File("board.json")} exists; run factory state-import\n"), Run("status"));

        Assert.Equal((0, "imported 1 ideas, 1 jobs, 1 events\ncheck: board.json and events.jsonl match the database\n", ""), Run("state-import"));
        Assert.Equal((1, "", "factory: state-import refused: the database already has a board\n"), Run("state-import"));
        Assert.Equal((0, "J-001  backlog    r1 Search\n", ""), Run("status"));
    }

    [Fact]
    public void A_transition_saves_to_the_database_and_both_exports_show_it()
    {
        Seed();
        Run("state-import");

        Assert.Equal(0, Run("approve", "J-001").Code);
        Assert.Equal(0, Run("event", "-Kind", "note", "-Job", "J-001", "-Detail", "from a hook").Code);

        var json = Run("status", "-Json").Out;
        Assert.Contains("\"stage\": \"approved\"", json, StringComparison.Ordinal);
        Assert.Equal(json.TrimEnd('\n'), System.IO.File.ReadAllText(File("board.json")).ReplaceLineEndings("\n"));
        var events = System.IO.File.ReadAllLines(File("events.jsonl"));
        Assert.Equal(3, events.Length);
        Assert.Contains("\"kind\":\"stage\",\"from\":\"backlog\",\"to\":\"approved\"", events[1], StringComparison.Ordinal);
        Assert.Contains("\"detail\":\"from a hook\",\"round\":1", events[2], StringComparison.Ordinal);
    }

    [Fact]
    public void An_edited_export_changes_nothing_and_the_next_command_rewrites_it()
    {
        Seed();
        Run("state-import");
        System.IO.File.WriteAllText(File("board.json"), "{ \"version\": 2, \"ideas\": [], \"jobs\": [] }");
        System.IO.File.Delete(File(".export.json"));

        Assert.Equal((0, "J-001  backlog    r1 Search\n", ""), Run("status"));
        Assert.Contains("\"id\": \"J-001\"", System.IO.File.ReadAllText(File("board.json")), StringComparison.Ordinal);
    }
}
