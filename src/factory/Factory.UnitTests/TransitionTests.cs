using System.Text.Json.Nodes;
using Factory.Core;

namespace Factory.UnitTests;

public class TransitionTests
{
    static readonly DateTimeOffset Now = new(2026, 11, 10, 14, 0, 5, 123, TimeSpan.Zero);

    /// <summary>
    /// Every (from, to, flag) the contract's "Job transitions" table allows, written out by hand from it,
    /// so the table in Factory.Core is checked against the contract, not against itself.
    /// </summary>
    public static TheoryData<string, string, string?> ContractRows()
    {
        var d = new TheoryData<string, string, string?>
        {
            { "backlog", "approved", null },
            { "backlog", "discarded", null },
            { "approved", "build", null },
            { "approved", "canary", null },
            { "canary", "build", null },
            { "canary", "review", null },
            { "canary", "deploy", null },
            { "canary", "approve", null },
            { "canary", "discarded", null },
            { "build", "review", null },
            { "review", "build", null },
            { "review", "deploy", null },
            { "review", "approve", null },
            { "deploy", "approve", null },
            { "deploy", "build", null },
            { "blocked", "approved", null },
            { "blocked", "discarded", null },
            { "ready", "approved", null },
            { "approved", "deploy", null },
            { "approved", "review", null },
            { "approved", "approve", null },
            { "approve", "gate", null },
            { "approve", "ready", "escalated" },
            { "gate", "ready", null },
            { "gate", "ready", "gate-failed" },
            { "gate", "ready", "escalated" },
            { "ready", "done", null },
            { "ready", "ready", "merge-blocked" },
            { "ready", "ready", "gate-failed" },
            { "ready", "discarded", null },
        };
        foreach (var to in new[] { "ready", "blocked" })
        {
            foreach (var r in new[] { "out-of-scope", "spec-stale" }) d.Add("build", to, r);
            d.Add("review", to, "max-rounds");
            foreach (var r in new[] { "deploy-fix-rounds", "deploy-failed", "environment", "github-permission" }) d.Add("deploy", to, r);
            foreach (var from in new[] { "build", "review", "deploy", "approve" }) d.Add(from, to, "usage-limit");
        }
        foreach (var from in new[] { "build", "review", "deploy" }) { d.Add(from, "blocked", "escalated"); d.Add(from, "ready", "escalated"); }
        return d;
    }

    [Theory]
    [MemberData(nameof(ContractRows))]
    public void Every_contract_row_is_allowed_and_moves_the_job(string from, string to, string? reason)
    {
        var j = NewJob(from);

        var e = Transitions.Move(j, to, "human", reason, reason is null ? null : "why not", null, Now);

        Assert.Equal(to, j.Stage);
        Assert.Equal("2026-11-10T14:00:05.123Z", (string?)j.Node["updatedAt"]);
        Assert.Equal(reason, j.Flag?.Reason);
        var h = Assert.IsType<JsonObject>(j.Node["history"]!.AsArray()[^1]);
        Assert.Equal($$"""{"ts":"2026-11-10T14:00:05.123Z","from":"{{from}}","to":"{{to}}","by":"human"}""", h.ToJsonString());
        Assert.Equal(from, (string?)e["from"]);
    }

    [Fact]
    public void The_table_allows_nothing_the_contract_does_not()
    {
        var contract = ContractRows().Select(r => r.Data).ToHashSet();
        var table = Transitions.Rows.SelectMany(r => r.From.SelectMany(f => r.To.Select(t => (f, t, r.Reason)))).ToHashSet();

        Assert.Empty(table.Except(contract));
        Assert.Empty(contract.Except(table));
    }

    [Theory]
    [InlineData("backlog", "build", null)]
    [InlineData("done", "approved", null)]
    [InlineData("discarded", "backlog", null)]
    [InlineData("ready", "done", "merge-blocked")]
    [InlineData("build", "ready", null)]
    [InlineData("approve", "blocked", "escalated")]
    [InlineData("gate", "blocked", "escalated")]
    [InlineData("review", "gate", null)]
    public void A_move_the_contract_does_not_allow_is_refused_as_set_stage_refuses_it(string from, string to, string? reason)
    {
        var j = NewJob(from);

        var x = Assert.Throws<FactoryException>(() => Transitions.Move(j, to, "orchestrator", reason, null, null, Now));

        Assert.Equal($"transition {from}->{to} refused (use the dedicated command, or see FACTORY-CONTRACT.md)", x.Message);
        Assert.Equal(1, x.ExitCode);
        Assert.Equal(from, j.Stage);
    }

    [Fact]
    public void Move_writes_the_job_and_its_stage_event_as_factory_ps1_does()
    {
        var j = new Job(JsonNode.Parse("""{"id":"J-007","stage":"build","round":2,"flag":null,"roundStart":"abc","updatedAt":"x","history":[]}""")!.AsObject());

        var e = Transitions.Move(j, "blocked", "runner", "escalated", "claude exited", "the long story", Now);

        Assert.Equal(
            """{"id":"J-007","stage":"blocked","round":2,"flag":{"reason":"escalated","detail":"claude exited","why":"the long story"},"roundStart":"abc","updatedAt":"2026-11-10T14:00:05.123Z","history":[{"ts":"2026-11-10T14:00:05.123Z","from":"build","to":"blocked","by":"runner"}]}""",
            j.Node.ToJsonString());
        Assert.Equal("""{"ts":"2026-11-10T14:00:05.123Z","job":"J-007","kind":"stage","from":"build","to":"blocked","by":"runner","reason":"escalated"}""", e.ToJsonString());
    }

    [Fact]
    public void Move_without_a_reason_clears_the_flag_and_a_move_into_build_clears_round_start()
    {
        var j = new Job(JsonNode.Parse("""{"id":"J-001","stage":"deploy","flag":{"reason":"x","detail":"y"},"roundStart":"abc","history":[]}""")!.AsObject());

        var e = Transitions.Move(j, "build", "orchestrator", null, null, null, Now);

        Assert.Null(j.Node["flag"]);
        Assert.True(j.Node.ContainsKey("roundStart"));
        Assert.Null(j.Node["roundStart"]);
        Assert.False(e.ContainsKey("reason"));
    }

    [Fact]
    public void A_flag_with_no_detail_has_an_empty_detail_and_no_why()
    {
        var j = NewJob("gate");

        Transitions.Move(j, "ready", "gate", "gate-failed", null, "", Now);

        Assert.Equal("""{"reason":"gate-failed","detail":""}""", j.Node["flag"]!.ToJsonString());
    }

    [Fact]
    public void Keys_a_job_lacks_are_added_in_factory_ps1_order()
    {
        var j = new Job(JsonNode.Parse("""{"id":"J-001","stage":"backlog"}""")!.AsObject());

        Transitions.Move(j, "approved", "human", null, null, null, Now);

        Assert.Equal(["id", "stage", "updatedAt", "flag", "history"], j.Node.Select(p => p.Key));
        // @($null) + entry: a job with no history gets a leading null, as factory.ps1 writes it.
        Assert.Equal(2, j.Node["history"]!.AsArray().Count);
    }

    [Theory]
    [InlineData(true, false, "build", "blocked")]
    [InlineData(true, false, "review", "blocked")]
    [InlineData(true, false, "deploy", "blocked")]
    [InlineData(true, false, "approve", "ready")]
    [InlineData(true, false, "gate", "ready")]
    [InlineData(true, true, "build", "ready")]
    [InlineData(false, false, "build", "ready")]
    public void Escalation_blocks_a_deployable_job_before_it_has_a_preview(bool deploysOn, bool bootstrap, string stage, string expected)
    {
        var j = NewJob(stage);
        if (bootstrap) j.Node["kind"] = "bootstrap";

        Assert.Equal(expected, Transitions.EscalationStage(j, deploysOn));
    }

    [Fact]
    public void Require_stage_names_the_job_its_stage_and_the_stages_needed()
    {
        var x = Assert.Throws<FactoryException>(() => Transitions.RequireStage(NewJob("build"), ["ready", "blocked"], "rework"));
        Assert.Equal("rework refused: J-001 is in 'build', needs ready|blocked", x.Message);

        Transitions.RequireStage(NewJob("READY"), ["ready"], "merge"); // -notin ignores case
    }

    [Fact]
    public void Reset_reviews_sets_every_role_pending_in_contract_order()
    {
        var j = NewJob("review");
        j.Node["reviews"] = new JsonObject { ["ui"] = "PASS" };

        Transitions.ResetReviews(j);

        Assert.Equal("""{"security":"pending","code":"pending","ux":"pending","ui":"pending"}""", j.Node["reviews"]!.ToJsonString());
    }

    internal static Job NewJob(string stage, string id = "J-001") =>
        new(new JsonObject { ["id"] = id, ["stage"] = stage, ["round"] = 1, ["flag"] = null, ["updatedAt"] = null, ["history"] = new JsonArray() });
}
