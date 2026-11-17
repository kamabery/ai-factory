using Factory.Cli;
using Factory.Core;
using NSubstitute;

namespace Factory.UnitTests;

/// <summary>set-review, read-reviews and read-decision: the review round's records.</summary>
public class ReviewCommandsTests : CommandTestBase
{
    [Fact]
    public void Set_review_records_a_role_matching_its_key_without_case()
    {
        var s = FakeSession.With(Jobs.New("J-001", "review"));

        new SetReviewCommand(Clock).Run(Args("set-review", "J-001", "Code", "running"), s);

        Assert.Equal(("running", Now, 1), ((string?)s.Job("J-001")["reviews"]!["code"], (string?)s.Job("J-001")["updatedAt"], s.Saves));
        Assert.False(s.Job("J-001")["reviews"]!.AsObject().ContainsKey("Code"));
    }

    [Theory]
    [InlineData("perf", "PASS", "unknown role perf")]
    [InlineData("code", "MAYBE", "unknown review state MAYBE")]
    public void Set_review_refuses_an_unknown_role_or_state(string role, string state, string message)
    {
        var s = FakeSession.With(Jobs.New("J-001", "review"));

        Assert.Equal(message, Assert.Throws<FactoryException>(() => new SetReviewCommand(Clock).Run(Args("set-review", "J-001", role, state), s)).Message);
    }

    private void Reviews(string verdict, string? ux = null)
    {
        foreach (var role in Stages.Roles) Files.Put(JobFile("J-001", $"round-1/review-{role}.md"), (role == "ux" && ux is not null ? ux : verdict) + "\n\n# review");
    }

    [Fact]
    public void Read_reviews_with_every_pass_and_deploys_off_goes_to_approve_with_a_note_and_prints_the_stage()
    {
        var s = FakeSession.With(Jobs.New("J-001", "review"));
        Reviews("Verdict: PASS");

        var r = new ReadReviewsCommand(Paths, Files, DotEnv, Clock).Run(Args("read-reviews", "J-001"), s);

        Assert.Equal(["approve"], r.Out);
        Assert.Equal(["stage", "note"], s.Kinds);
    }

    [Theory]
    [InlineData("IaC/main.tf")]
    [InlineData(".aspirestarter.json")]
    public void Read_reviews_with_deploys_on_goes_to_deploy_when_the_clone_has_infra(string infra)
    {
        DotEnv.Get("HCP_ORG").Returns("org");
        Files.Put(Path.Combine([Work, "jobs", "J-001", .. infra.Split('/')]), "x");
        var s = FakeSession.With(Jobs.New("J-001", "review"));
        Reviews("Verdict: PASS");

        Assert.Equal(["deploy"], new ReadReviewsCommand(Paths, Files, DotEnv, Clock).Run(Args("read-reviews", "J-001"), s).Out);
    }

    [Fact]
    public void Read_reviews_with_deploys_on_skips_deploy_for_a_clone_with_nothing_to_deploy()
    {
        DotEnv.Get("HCP_ORG").Returns("org");
        Files.Put(Path.Combine(Work, "jobs", "J-001", ".git", "HEAD"), "ref: refs/heads/factory/J-001");
        Files.Put(Path.Combine(Work, "jobs", "J-001", "src", "Hello", "Program.cs"), "x");
        var s = FakeSession.With(Jobs.New("J-001", "review"));
        Reviews("Verdict: PASS");

        var r = new ReadReviewsCommand(Paths, Files, DotEnv, Clock).Run(Args("read-reviews", "J-001"), s);

        Assert.Equal(["approve"], r.Out);
        Assert.Equal(["stage", "note"], s.Kinds);
    }

    [Fact]
    public void Read_reviews_saves_the_verdicts_it_read_before_refusing_a_missing_one()
    {
        var s = FakeSession.With(Jobs.New("J-001", "review"));
        Reviews("Verdict: PASS", ux: "verdict: pass");

        var e = Assert.Throws<FactoryException>(() => new ReadReviewsCommand(Paths, Files, DotEnv, Clock).Run(Args("read-reviews", "J-001"), s));

        Assert.Equal(("no verdict from: ux", 2), (e.Message, e.ExitCode));
        Assert.Equal((1, "PASS"), (s.Saves, (string?)s.Job("J-001")["reviews"]!["code"]));
        Assert.Empty(s.Events);
    }

    [Fact]
    public void Read_decision_moves_an_approved_job_to_gate()
    {
        var s = FakeSession.With(Jobs.New("J-001", "approve"));
        Files.Put(JobFile("J-001", "decision.md"), "Decision: APPROVE\n");

        var r = new ReadDecisionCommand(Paths, Files, Clock).Run(Args("read-decision", "J-001"), s);

        Assert.Equal(["gate"], r.Out);
        Assert.Equal(("APPROVE", 1), ((string?)s.Job("J-001")["decision"], s.Saves));
    }

    [Fact]
    public void Read_decision_without_a_decision_line_exits_2()
    {
        var s = FakeSession.With(Jobs.New("J-001", "approve"));

        var e = Assert.Throws<FactoryException>(() => new ReadDecisionCommand(Paths, Files, Clock).Run(Args("read-decision", "J-001"), s));

        Assert.Equal(("no decision line in decision.md", 2, 0), (e.Message, e.ExitCode, s.Saves));
    }
}
