using System.Text.Json.Nodes;
using Factory.Core;

namespace Factory.UnitTests;

public class VerdictTests
{
    static readonly DateTimeOffset Now = new(2026, 11, 10, 14, 0, 0, TimeSpan.Zero);

    static Job InReview(int round = 1, string? kind = null)
    {
        var j = TransitionTests.NewJob("review");
        j.Node["round"] = round;
        if (kind is not null) j.Node["kind"] = kind;
        j.Node["reviews"] = new JsonObject { ["security"] = "running", ["code"] = "running", ["ux"] = "running", ["ui"] = "running" };
        return j;
    }

    static Func<string, string?> Verdicts_(string security = "Verdict: PASS", string code = "Verdict: PASS", string ux = "Verdict: PASS", string ui = "Verdict: PASS") =>
        role => role switch { "security" => security, "code" => code, "ux" => ux, _ => ui };

    [Theory]
    [InlineData("Verdict: PASS", "PASS")]
    [InlineData("Verdict: CHANGES", "CHANGES")]
    [InlineData("Verdict: pass", null)]
    [InlineData("verdict: PASS", null)]
    [InlineData("Verdict:  PASS", null)]
    [InlineData("Verdict: PASS.", null)]
    [InlineData(null, null)]
    public void A_review_verdict_is_exactly_its_first_line(string? line, string? verdict)
    {
        Assert.Equal(verdict, Verdicts.Review(line));
    }

    [Theory]
    [InlineData("Rating: good", "good")]
    [InlineData("Rating: poor", "poor")]
    [InlineData("Rating: Good", null)]
    [InlineData("Rating: great", null)]
    public void A_rating_line_is_exactly_good_mixed_or_poor(string line, string? rating)
    {
        Assert.Equal(rating, Verdicts.Rating(line));
    }

    [Fact]
    public void Read_reviews_with_a_missing_verdict_exits_2_and_keeps_the_verdicts_it_read()
    {
        var j = InReview();

        var x = Assert.Throws<FactoryException>(() => Verdicts.ReadReviews(j, Verdicts_(code: "Verdict: CHANGES", ux: "", ui: "LGTM"), false, Now));

        Assert.Equal("no verdict from: ux, ui", x.Message);
        Assert.Equal(2, x.ExitCode);
        Assert.True(x.KeepChanges);
        Assert.Equal("""{"security":"PASS","code":"CHANGES","ux":"running","ui":"running"}""", j.Node["reviews"]!.ToJsonString());
        Assert.Equal("review", j.Stage);
    }

    [Fact]
    public void Read_reviews_outside_review_is_refused()
    {
        var j = TransitionTests.NewJob("build");

        var x = Assert.Throws<FactoryException>(() => Verdicts.ReadReviews(j, Verdicts_(), false, Now));

        Assert.Equal("read-reviews refused: J-001 is in 'build', needs review", x.Message);
    }

    [Fact]
    public void All_pass_with_deploys_on_goes_to_deploy()
    {
        var j = InReview();

        var events = Verdicts.ReadReviews(j, Verdicts_(), deploysOn: true, Now);

        Assert.Equal("deploy", j.Stage);
        Assert.Single(events);
    }

    [Fact]
    public void All_pass_with_deploys_off_goes_to_approve_with_a_note()
    {
        var j = InReview();

        var events = Verdicts.ReadReviews(j, Verdicts_(), deploysOn: false, Now);

        Assert.Equal("approve", j.Stage);
        Assert.Equal("""{"ts":"2026-11-10T14:00:00.000Z","job":"J-001","kind":"note","detail":"deploy skipped: no HCP_ORG in .env"}""", events[1].ToJsonString());
    }

    [Fact]
    public void All_pass_on_a_bootstrap_goes_to_approve_whatever_the_deploys()
    {
        var j = InReview(kind: "bootstrap");

        var events = Verdicts.ReadReviews(j, Verdicts_(), deploysOn: true, Now);

        Assert.Equal("approve", j.Stage);
        Assert.Equal(Verdicts.BootstrapNoDeploy, (string?)events[1]["detail"]);
    }

    [Fact]
    public void Changes_at_round_1_start_round_2_with_reviews_pending()
    {
        var j = InReview(round: 1);

        Verdicts.ReadReviews(j, Verdicts_(ux: "Verdict: CHANGES"), true, Now);

        Assert.Equal("build", j.Stage);
        Assert.Equal(2, j.Round);
        Assert.Equal("""{"security":"pending","code":"pending","ux":"pending","ui":"pending"}""", j.Node["reviews"]!.ToJsonString());
    }

    [Theory]
    [InlineData(true, "blocked")]
    [InlineData(false, "ready")]
    public void Changes_at_round_2_stop_the_job_with_max_rounds(bool deploysOn, string stage)
    {
        var j = InReview(round: 2);

        Verdicts.ReadReviews(j, Verdicts_(code: "Verdict: CHANGES", ui: "Verdict: CHANGES"), deploysOn, Now);

        Assert.Equal(stage, j.Stage);
        Assert.Equal("""{"reason":"max-rounds","detail":"CHANGES from code, ui at round 2"}""", j.Node["flag"]!.ToJsonString());
    }

    [Fact]
    public void Approve_moves_to_gate_and_records_the_decision()
    {
        var j = TransitionTests.NewJob("approve");

        Verdicts.ReadDecision(j, "Decision: APPROVE\n\nall good", Now);

        Assert.Equal("gate", j.Stage);
        Assert.Equal("APPROVE", (string?)j.Node["decision"]);
    }

    [Fact]
    public void Escalate_copies_the_escalation_reason_into_the_flag()
    {
        var j = TransitionTests.NewJob("approve");

        Verdicts.ReadDecision(j, "Decision: ESCALATE\r\n\r\n## Escalation reason\r\n  The preview 500s.  \r\nMore detail.\r\n\r\n## Notes\r\nx", Now);

        Assert.Equal("ready", j.Stage);
        Assert.Equal("ESCALATE", (string?)j.Node["decision"]);
        Assert.Equal("""{"reason":"escalated","detail":"The preview 500s.","why":"The preview 500s.  \nMore detail."}""", j.Node["flag"]!.ToJsonString());
    }

    [Fact]
    public void Escalate_with_no_reason_points_at_decision_md()
    {
        var j = TransitionTests.NewJob("approve");

        Verdicts.ReadDecision(j, "Decision: ESCALATE\n## Escalation reason\nN/A", Now);

        Assert.Equal("""{"reason":"escalated","detail":"approver escalated; see decision.md"}""", j.Node["flag"]!.ToJsonString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Decision: approve")]
    [InlineData("I approve")]
    public void A_missing_or_malformed_decision_exits_2_and_does_not_move(string? text)
    {
        var j = TransitionTests.NewJob("approve");

        var x = Assert.Throws<FactoryException>(() => Verdicts.ReadDecision(j, text, Now));

        Assert.Equal("no decision line in decision.md", x.Message);
        Assert.Equal(2, x.ExitCode);
        Assert.Equal("approve", j.Stage);
    }

    [Fact]
    public void Read_decision_outside_approve_is_refused()
    {
        var x = Assert.Throws<FactoryException>(() => Verdicts.ReadDecision(TransitionTests.NewJob("gate"), "Decision: APPROVE", Now));

        Assert.Equal("read-decision refused: J-001 is in 'gate', needs approve", x.Message);
    }
}
