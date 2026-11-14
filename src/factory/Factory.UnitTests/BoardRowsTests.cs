using System.Text.Json.Nodes;
using Factory.Core;
using Factory.Data;
using Factory.Data.Abstractions;

namespace Factory.UnitTests;

public class BoardRowsTests
{
    private const string Text = """
        {"version":2,"seq":{"I":2,"J":3},"ideas":[{"id":"I-001","status":"done","target":"o/a","jobs":["J-001"]},{"id":"I-002","status":"drafting"}],"jobs":[{"id":"J-001","idea":"I-001","stage":"review","round":2,"target":"o/a","kind":"feature","costUsd":1.84},{"id":"J-002","stage":"backlog","round":1,"flag":null}],"later":true}
        """;

    private static JsonObject Board() => Factory.Core.Board.Parse(Text);

    private static BoardRead Read(JsonObject board)
    {
        var ideas = board.Ideas().Select((i, n) => (i.Id, new RowRead(PsJson.WriteCompact(i.Node), 7, n))).ToDictionary(x => x.Id, x => x.Item2);
        var jobs = board.Jobs().Select((j, n) => (j.Id, new RowRead(PsJson.WriteCompact(j.Node), 5, n))).ToDictionary(x => x.Id, x => x.Item2);
        return new BoardRead(board, BoardRows.Skeleton(board), 3, ideas, jobs);
    }

    [Fact]
    public void The_skeleton_keeps_every_key_in_place_with_ideas_and_jobs_empty() =>
        Assert.Equal("""{"version":2,"seq":{"I":2,"J":3},"ideas":[],"jobs":[],"later":true}""", BoardRows.Skeleton(Board()));

    [Fact]
    public void Assembling_the_rows_gives_back_the_board_byte_for_byte()
    {
        var b = Board();

        var back = BoardRows.Assemble(BoardRows.Skeleton(b), b.Ideas().Select(i => PsJson.WriteCompact(i.Node)), b.Jobs().Select(j => PsJson.WriteCompact(j.Node)));

        Assert.Equal(PsJson.Write(b, "\r\n"), PsJson.Write(back, "\r\n"));
    }

    [Fact]
    public void A_skeleton_with_no_jobs_key_gets_one_only_when_there_are_jobs()
    {
        Assert.Equal("""{"version":2,"ideas":[]}""", BoardRows.Assemble("""{"version":2,"ideas":[]}""", [], []).ToJsonString());
        Assert.Equal("""{"version":2,"ideas":[],"jobs":[{"id":"J-001"}]}""", BoardRows.Assemble("""{"version":2,"ideas":[]}""", [], ["""{"id":"J-001"}"""]).ToJsonString());
    }

    [Fact]
    public void An_unchanged_board_plans_nothing()
    {
        var b = Board();

        var plan = BoardRows.Plan(Read(b), b);

        Assert.False(plan.SkeletonChanged);
        Assert.Empty(plan.Rows);
    }

    [Fact]
    public void A_changed_job_is_an_update_at_its_read_version_and_place()
    {
        var b = Board();
        var read = Read(b);
        b.Job("J-002").Node["stage"] = "approved";

        var c = Assert.Single(BoardRows.Plan(read, b).Rows);

        Assert.Equal((RowKind.Job, ChangeKind.Update, "J-002", 5L, 1L), (c.Kind, c.Change, c.Id, c.ReadVersion, c.Ordinal));
        Assert.Equal("""{"id":"J-002","stage":"approved","round":1,"flag":null}""", c.Json);
    }

    [Fact]
    public void A_new_idea_and_job_are_inserted_after_the_last_place_and_the_seq_changes_the_skeleton()
    {
        var b = Board();
        var read = Read(b);
        b["seq"]!["J"] = 4;
        b["ideas"]!.AsArray().Add(new JsonObject { ["id"] = "I-003" });
        b["jobs"]!.AsArray().Add(new JsonObject { ["id"] = "J-004", ["stage"] = "backlog" });

        var plan = BoardRows.Plan(read, b);

        Assert.True(plan.SkeletonChanged);
        Assert.Equal("""{"version":2,"seq":{"I":2,"J":4},"ideas":[],"jobs":[],"later":true}""", plan.Skeleton);
        Assert.Equal([(RowKind.Idea, ChangeKind.Insert, "I-003", 2L), (RowKind.Job, ChangeKind.Insert, "J-004", 2L)],
            plan.Rows.Select(r => (r.Kind, r.Change, r.Id, r.Ordinal)));
    }

    [Fact]
    public void A_purged_job_is_a_delete_and_the_rest_keep_their_places()
    {
        var b = Board();
        var read = Read(b);
        b["jobs"] = new JsonArray(b.Job("J-002").Node.DeepClone());

        var c = Assert.Single(BoardRows.Plan(read, b).Rows);

        Assert.Equal((RowKind.Job, ChangeKind.Delete, "J-001", 5L), (c.Kind, c.Change, c.Id, c.ReadVersion));
    }

    [Fact]
    public void A_reordered_board_renumbers_every_place()
    {
        var b = Board();
        var read = Read(b);
        b["jobs"] = new JsonArray(b.Job("J-002").Node.DeepClone(), b.Job("J-001").Node.DeepClone());

        var plan = BoardRows.Plan(read, b);

        Assert.Equal([("J-002", 0L), ("J-001", 1L)], plan.Rows.Select(r => (r.Id, r.Ordinal)));
        Assert.All(plan.Rows, r => Assert.Equal(ChangeKind.Update, r.Change));
    }

    [Fact]
    public void A_first_save_inserts_everything_and_the_skeleton()
    {
        var b = Board();

        var plan = BoardRows.Plan(BoardRead.None(), b);

        Assert.True(plan.SkeletonChanged);
        Assert.Equal(["I-001", "I-002", "J-001", "J-002"], plan.Rows.Select(r => r.Id));
        Assert.All(plan.Rows, r => Assert.Equal(ChangeKind.Insert, r.Change));
    }

    [Theory]
    [InlineData("""{"version":2,"ideas":[],"jobs":[{"stage":"backlog"}]}""", "the board has a job with no id")]
    [InlineData("""{"version":2,"ideas":[{"id":"I-001"},{"id":"I-001"}],"jobs":[]}""", "the board has two ideas I-001")]
    public void A_job_with_no_id_or_a_repeated_id_is_refused(string board, string message) =>
        Assert.Equal(message, Assert.Throws<FactoryException>(() => BoardRows.Plan(BoardRead.None(), Factory.Core.Board.Parse(board))).Message);

    [Theory]
    [InlineData("""{"ts":"2026-11-14T10:00:00Z","job":"J-001","kind":"stage","from":"a"}""", "2026-11-14T10:00:00Z", "J-001", null, "stage")]
    [InlineData("""{"ts":"2026-11-14T10:00:00Z","idea":"I-002","kind":"idea","round":1}""", "2026-11-14T10:00:00Z", null, "I-002", "idea")]
    [InlineData("not json", null, null, null, null)]
    [InlineData("[1,2]", null, null, null, null)]
    public void An_events_line_gives_its_columns(string line, string? ts, string? job, string? idea, string? kind) =>
        Assert.Equal(new EventColumns(ts, job, idea, kind), BoardRows.Columns(line));
}
