using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using Factory.Core;

namespace Factory.UnitTests;

public class ContractDataTests
{
    static readonly DateTimeOffset Now = new(2026, 11, 10, 14, 0, 0, 7, TimeSpan.Zero);

    /// <summary>A sanitized copy of a real state file, embedded in this assembly (unit tests read no files).</summary>
    static string Golden(string name)
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream($"Golden/{name}")!;
        using var r = new StreamReader(s, new UTF8Encoding(false));
        return r.ReadToEnd();
    }

    [Fact]
    public void A_real_board_json_round_trips_byte_for_byte()
    {
        var text = Golden("board.json");

        Assert.Equal(text, PsJson.Write(Board.Parse(text), "\r\n"));
    }

    [Fact]
    public void A_real_events_jsonl_round_trips_byte_for_byte()
    {
        var text = Golden("events.jsonl");
        var lines = text.Split('\n')[..^1];

        Assert.Equal(text, string.Concat(lines.Select(l => PsJson.WriteCompact(JsonNode.Parse(l)) + "\n")));
        Assert.All(lines, l => Events.RequireKind((string?)JsonNode.Parse(l)!["kind"]));
    }

    [Fact]
    public void Every_job_on_the_real_board_reads_through_the_typed_view()
    {
        var b = Board.Parse(Golden("board.json"));

        Assert.All(b.Jobs(), j =>
        {
            Assert.Matches("^J-\\d{3}$", j.Id);
            Assert.Contains(j.Stage, Stages.All);
            Assert.True(j.Round >= 1);
            if (j.Flag is { } f) Assert.False(string.IsNullOrEmpty(f.Reason));
        });
        Assert.All(b.Ideas(), i => Assert.Matches("^I-\\d{3}$", i.Id));
    }

    [Fact]
    public void Compact_json_is_convert_to_json_compress()
    {
        var o = new JsonObject { ["ts"] = "t", ["a"] = new JsonArray(1, "x\u2028\"", null), ["e"] = new JsonObject(), ["n"] = 1.5 };

        Assert.Equal("{\"ts\":\"t\",\"a\":[1,\"x\\u2028\\\"\",null],\"e\":{},\"n\":1.5}", PsJson.WriteCompact(o));
    }

    [Fact]
    public void An_event_starts_with_its_time_then_the_fields_in_order()
    {
        var e = Events.Create(Now, ("job", "J-001"), ("kind", "note"), ("detail", "x"));

        Assert.Equal("""{"ts":"2026-11-10T14:00:00.007Z","job":"J-001","kind":"note","detail":"x"}""", PsJson.WriteCompact(e));
    }

    [Theory]
    [InlineData("stage")]
    [InlineData("canary")]
    [InlineData("Note")]
    public void Event_kinds_are_the_contracts_matched_without_case(string kind)
    {
        Events.RequireKind(kind);
    }

    [Theory]
    [InlineData("bogus")]
    [InlineData(null)]
    public void An_unknown_event_kind_is_refused(string? kind)
    {
        var x = Assert.Throws<FactoryException>(() => Events.RequireKind(kind));
        Assert.Equal($"unknown event kind '{kind}'", x.Message);
    }

    [Fact]
    public void Times_are_utc_with_milliseconds_and_z()
    {
        Assert.Equal("2026-11-10T14:00:00.007Z", Iso.Format(new DateTimeOffset(2026, 11, 10, 9, 0, 0, 7, TimeSpan.FromHours(-5))));
    }

    [Fact]
    public void Ids_go_one_above_the_larger_of_seq_and_the_highest_on_the_board()
    {
        var b = Board.Parse("""{"version":2,"jobs":[{"id":"J-004"},{"id":"J-002"}],"ideas":[]}""");

        Assert.Equal("J-005", Ids.Next(b, "J", b["jobs"]!.AsArray()));
        Assert.Equal("""{"J":5}""", b["seq"]!.ToJsonString());

        b["seq"]!["J"] = 41;
        Assert.Equal("J-042", Ids.Next(b, "J", b["jobs"]!.AsArray()));
        Assert.Equal("I-001", Ids.Next(b, "I", b["ideas"]!.AsArray()));
        Assert.Equal("""{"J":42,"I":1}""", b["seq"]!.ToJsonString());
        Assert.Equal(["version", "jobs", "ideas", "seq"], b.Select(p => p.Key));
    }

    [Fact]
    public void A_job_id_with_four_digits_still_counts()
    {
        var b = Board.Parse("""{"jobs":[{"id":"J-1000"}],"seq":{"J":3}}""");

        Assert.Equal("J-1001", Ids.Next(b, "J", b["jobs"]!.AsArray()));
    }

    [Fact]
    public void No_job_with_that_id_is_refused()
    {
        var x = Assert.Throws<FactoryException>(() => Board.Empty().Job("J-404"));
        Assert.Equal("no job J-404", x.Message);
    }

    [Fact]
    public void Typed_views_read_the_contract_fields()
    {
        var j = new Job(JsonNode.Parse("""
            {"id":"J-009","idea":"I-003","stage":"blocked","round":3,"kind":"bootstrap","target":"o/a","approvedAt":"t",
             "flag":{"reason":"usage-limit","detail":"d","why":"w","stage":"review","session":"s1"},
             "deploy":{"status":"failed","attempts":2,"destroyStartedAt":"t2","destroyFailures":1},"resume":"deploy"}
            """)!.AsObject());

        Assert.Equal(("J-009", "I-003", "o/a", "t", "deploy"), (j.Id, j.Idea, j.Target, j.ApprovedAt, j.Resume));
        Assert.True(j.IsBootstrap);
        Assert.Equal(("usage-limit", "d", "w", "review", "s1"), (j.Flag!.Reason, j.Flag.Detail, j.Flag.Why, j.Flag.Stage, j.Flag.Session));
        Assert.Equal(("failed", 2, "t2", 1), (j.Deploy!.Status, j.Deploy.Attempts, j.Deploy.DestroyStartedAt, j.Deploy.DestroyFailures));
        Assert.Null(TransitionTests.NewJob("build").Deploy);
    }
}
