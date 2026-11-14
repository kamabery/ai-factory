using System.Text.Json.Nodes;
using Factory.Core;
using Factory.Data;
using Factory.Data.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Factory.IntegrationTests;

/// <summary>
/// The database rows (contract, "State backends", Database), run on every provider: SQLite in memory and in a file,
/// and SQL Server. Two <see cref="EfBoardRows"/> on one database stand in for two commands.
/// </summary>
public abstract class BoardRowsContract
{
    protected abstract IDbConnectionInfo Db { get; }

    private EfBoardRows Rows() => new(new FactoryDbs(Db));

    private static JsonObject Two() => Board.Parse("""
        {"version":2,"seq":{"I":1,"J":2},"ideas":[{"id":"I-001","status":"done","text":"two — ünï","jobs":["J-001","J-002"]}],"jobs":[{"id":"J-001","idea":"I-001","stage":"backlog","round":1,"costUsd":1.84},{"id":"J-002","idea":"I-001","stage":"ready","round":2,"flag":null}]}
        """);

    private BoardRead Seeded()
    {
        var rows = Rows();
        rows.Save(BoardRead.None(), Two());
        return rows.Read()!;
    }

    [Fact]
    public void An_empty_database_has_no_board_and_revision_0()
    {
        Assert.Null(Rows().Read());
        Assert.Equal(0, Rows().Revision());
    }

    [Fact]
    public void A_saved_board_reads_back_byte_for_byte_and_bumps_the_revision()
    {
        Rows().Save(BoardRead.None(), Two());

        var back = Rows().Read()!;

        Assert.Equal(PsJson.Write(Two(), "\n"), PsJson.Write(back.Board, "\n"));
        Assert.Equal(1, Rows().Revision());
        Assert.Equal((1L, 1L, 1L), (back.Version, back.Jobs["J-001"].Version, back.Ideas["I-001"].Version));
    }

    [Fact]
    public void A_session_saves_again_on_the_versions_its_last_save_returned()
    {
        var read = Seeded();
        var rows = Rows();
        read.Board.Job("J-001").Node["stage"] = "approved";
        read = rows.Save(read, read.Board);
        read.Board.Job("J-001").Node["stage"] = "build";
        rows.Save(read, read.Board);

        Assert.Equal("build", Rows().Read()!.Board.Job("J-001").Stage);
        Assert.Equal(3, Rows().Read()!.Jobs["J-001"].Version);
    }

    [Fact]
    public void Two_commands_on_different_jobs_both_save()
    {
        var a = Seeded();
        var b = Rows().Read()!;
        a.Board.Job("J-001").Node["stage"] = "approved";
        b.Board.Job("J-002").Node["stage"] = "done";

        Rows().Save(a, a.Board);
        Rows().Save(b, b.Board);

        var now = Rows().Read()!.Board;
        Assert.Equal(("approved", "done"), (now.Job("J-001").Stage, now.Job("J-002").Stage));
    }

    [Fact]
    public void The_second_command_to_save_the_same_job_is_refused_and_saves_nothing()
    {
        var a = Seeded();
        var b = Rows().Read()!;
        a.Board.Job("J-002").Node["stage"] = "done";
        b.Board.Job("J-002").Node["stage"] = "build";
        b.Board.Job("J-001").Node["stage"] = "approved";
        Rows().Save(a, a.Board);

        var e = Assert.Throws<FactoryException>(() => Rows().Save(b, b.Board));

        Assert.Equal("board conflict: J-002 changed since this command read it; nothing was saved", e.Message);
        var now = Rows().Read()!.Board;
        Assert.Equal(("backlog", "done"), (now.Job("J-001").Stage, now.Job("J-002").Stage));
        Assert.Equal(2, Rows().Revision());
    }

    [Fact]
    public void Two_new_ideas_at_once_conflict_on_the_board_s_seq()
    {
        var a = Seeded();
        var b = Rows().Read()!;
        a.Board["seq"]!["I"] = 2;
        a.Board["ideas"]!.AsArray().Add(new JsonObject { ["id"] = "I-002" });
        b.Board["seq"]!["I"] = 2;
        b.Board["ideas"]!.AsArray().Add(new JsonObject { ["id"] = "I-002" });
        Rows().Save(a, a.Board);

        Assert.Equal("board conflict: the board changed since this command read it; nothing was saved",
            Assert.Throws<FactoryException>(() => Rows().Save(b, b.Board)).Message);
    }

    [Fact]
    public void Two_first_saves_conflict_on_the_board()
    {
        Rows().Save(BoardRead.None(), Two());

        Assert.Equal("board conflict: the board changed since this command read it; nothing was saved",
            Assert.Throws<FactoryException>(() => Rows().Save(BoardRead.None(), Two())).Message);
    }

    [Fact]
    public void A_purge_deletes_its_jobs_unless_another_command_changed_one_and_the_order_stays()
    {
        var a = Seeded();
        var b = Rows().Read()!;
        b.Board["jobs"] = new JsonArray(b.Board.Job("J-002").Node.DeepClone());
        a.Board.Job("J-001").Node["stage"] = "approved";
        Rows().Save(a, a.Board);

        Assert.Equal("board conflict: J-001 changed since this command read it; nothing was saved",
            Assert.Throws<FactoryException>(() => Rows().Save(b, b.Board)).Message);

        var c = Rows().Read()!;
        c.Board["jobs"] = new JsonArray(c.Board.Job("J-002").Node.DeepClone());
        c = Rows().Save(c, c.Board);
        c.Board["jobs"]!.AsArray().Add(new JsonObject { ["id"] = "J-003", ["stage"] = "backlog" });
        Rows().Save(c, c.Board);
        Assert.Equal(["J-002", "J-003"], Rows().Read()!.Board.Jobs().Select(j => j.Id));
    }

    [Fact]
    public void Events_append_in_order_bump_the_revision_and_start_a_board_when_there_is_none()
    {
        var rows = Rows();
        rows.AddEvent("""{"ts":"2026-11-14T10:00:00Z","idea":"I-001","kind":"idea","detail":"drafting"}""");
        rows.AddEvent("not json");

        var all = rows.EventsAfter(0);

        Assert.Equal(["""{"ts":"2026-11-14T10:00:00Z","idea":"I-001","kind":"idea","detail":"drafting"}""", "not json"], all.Select(e => e.Line));
        Assert.Equal(["not json"], rows.EventsAfter(all[0].Seq).Select(e => e.Line));
        Assert.Equal(2, rows.Revision());
        Assert.Equal(Board.Empty().ToJsonString(), rows.Read()!.Board.ToJsonString());
    }

    [Fact]
    public void An_import_writes_the_board_and_every_event_in_order_and_refuses_a_second()
    {
        var events = Enumerable.Range(1, 40).Select(n => $$"""{"ts":"2026-11-{{n % 28 + 1:00}}T10:00:00Z","job":"J-00{{n % 2 + 1}}","kind":"note","detail":"{{n}}"}""").ToList();

        var r = Rows().Import(Two(), events);

        Assert.Equal(new ImportResult(1, 2, 40), r);
        Assert.Equal(PsJson.Write(Two(), "\n"), PsJson.Write(Rows().Read()!.Board, "\n"));
        Assert.Equal(events, Rows().EventsAfter(0).Select(e => e.Line));
        Assert.Equal("state-import refused: the database already has a board",
            Assert.Throws<FactoryException>(() => Rows().Import(Two(), [])).Message);
    }

    [Fact]
    public void An_import_of_a_board_with_a_repeated_id_writes_nothing()
    {
        var bad = Board.Parse("""{"version":2,"ideas":[],"jobs":[{"id":"J-001"},{"id":"J-001"}]}""");

        Assert.Equal("the board has two jobs J-001", Assert.Throws<FactoryException>(() => Rows().Import(bad, ["x"])).Message);
        Assert.Null(Rows().Read());
    }

    [Fact]
    public void A_lease_is_one_holder_s_until_it_is_released_or_lapses()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var a = new EfLeaseRows(new FactoryDbs(Db), clock);
        var b = new EfLeaseRows(new FactoryDbs(Db), clock);

        var id = a.Acquire("runner", 0)!;
        Assert.Null(b.Acquire("runner", 0));
        Assert.True(a.Renew("runner", id));
        a.Release("runner", id);
        var other = b.Acquire("runner", 0)!;
        Assert.False(a.Renew("runner", id));

        clock.Advance(TimeSpan.FromSeconds(59));
        Assert.Null(a.Acquire("runner", 0));
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.NotNull(a.Acquire("runner", 0));
        Assert.False(b.Renew("runner", other));
        Assert.NotNull(b.Acquire("canary", 0));
    }
}
