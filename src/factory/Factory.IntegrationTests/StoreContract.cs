using System.Text.Json.Nodes;
using Factory.Core;
using Factory.Store;

namespace Factory.IntegrationTests;

/// <summary>
/// What every state backend owes a command (contract, "factory commands" and "State backends"): the board it saves is the
/// board the next command reads, board.json and events.jsonl show it as the file backend writes them, and a long step
/// reads the board again after. Run on local, azure (the blob stub), sqlite (in memory and in a file) and sql.
/// </summary>
public abstract class StoreContract : IDisposable
{
    protected readonly TempDir Dir = new();

    public virtual void Dispose() => Dir.Dispose();

    /// <summary>The backend's settings for a home; two homes on one shared backend are two machines.</summary>
    protected abstract StateOptions Options();

    private StoreHome Home(string name = "a") => new(Dir[name], Options());

    private static JsonObject Idea(string id) => new() { ["id"] = id, ["text"] = "find books — ünï", ["status"] = "drafting", ["jobs"] = new JsonArray() };

    private static JsonObject Job(string id, string stage) => new() { ["id"] = id, ["idea"] = "I-001", ["stage"] = stage, ["round"] = 1, ["costUsd"] = 1.84, ["flag"] = null };

    private static void Save(StoreHome home, Action<JsonObject> change, params JsonObject[] events)
    {
        using var s = home.Store.Open();
        change(s.Board);
        s.Save();
        foreach (var e in events) s.AddEvent(e);
        s.Close();
    }

    [Fact]
    public void A_new_home_reads_the_empty_board()
    {
        using var s = Home().Store.Open();

        Assert.Equal(Board.Empty().ToJsonString(), s.Board.ToJsonString());
    }

    [Fact]
    public void What_a_command_saves_the_next_reads_and_the_files_show_as_the_file_backend_writes_them()
    {
        var home = Home();
        Save(home, b =>
        {
            b["seq"] = new JsonObject { ["I"] = 1, ["J"] = 2 };
            b["ideas"]!.AsArray().Add(Idea("I-001"));
            b["jobs"]!.AsArray().Add(Job("J-001", "backlog"));
            b["jobs"]!.AsArray().Add(Job("J-002", "approved"));
        }, Events.Create(DateTimeOffset.Parse("2026-11-14T10:00:00Z"), ("idea", "I-001"), ("kind", "idea"), ("detail", "drafting")));
        Save(home, b => b.Job("J-002").Node["stage"] = "build",
            Events.Create(DateTimeOffset.Parse("2026-11-14T10:01:00Z"), ("job", "J-002"), ("kind", "stage"), ("from", "approved"), ("to", "build")));

        using var s = home.Store.Open();
        Assert.Equal(["backlog", "build"], s.Board.Jobs().Select(j => j.Stage));
        Assert.Equal(PsJson.Write(s.Board, Environment.NewLine), File.ReadAllText(home.Paths.BoardPath));
        Assert.Equal(
            "{\"ts\":\"2026-11-14T10:00:00.000Z\",\"idea\":\"I-001\",\"kind\":\"idea\",\"detail\":\"drafting\"}\n" +
            "{\"ts\":\"2026-11-14T10:01:00.000Z\",\"job\":\"J-002\",\"kind\":\"stage\",\"from\":\"approved\",\"to\":\"build\"}\n",
            File.ReadAllText(home.Paths.EventsPath));
    }

    [Fact]
    public void A_purge_removes_jobs_and_the_rest_keep_their_order()
    {
        var home = Home();
        Save(home, b => { foreach (var id in new[] { "J-001", "J-002", "J-003" }) b["jobs"]!.AsArray().Add(Job(id, "done")); });

        Save(home, b => b["jobs"] = new JsonArray(b.Job("J-001").Node.DeepClone(), b.Job("J-003").Node.DeepClone()));
        Save(home, b => b["jobs"]!.AsArray().Add(Job("J-004", "backlog")));

        using var s = home.Store.Open();
        Assert.Equal(["J-001", "J-003", "J-004"], s.Board.Jobs().Select(j => j.Id));
    }

    [Fact]
    public void A_long_step_reads_the_board_again_after_another_command_saved()
    {
        var home = Home();
        Save(home, b => b["jobs"]!.AsArray().Add(Job("J-001", "build")));

        using var s = home.Store.Open();
        s.Unlocked(() => Save(Home(), b => b.Job("J-001").Node["stage"] = "review"));

        Assert.Equal("review", s.Board.Job("J-001").Stage);
    }
}
