using System.Text.Json.Nodes;
using Factory.Core;

namespace Factory.UnitTests;

public class BacklogTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);

    private static (JsonObject Board, Idea Idea) Drafting(string? @base = null)
    {
        var idea = new JsonObject { ["id"] = "I-001", ["text"] = "x", ["target"] = "o/r", ["base"] = @base, ["status"] = "drafting", ["jobs"] = new JsonArray() };
        var board = new JsonObject { ["version"] = 2, ["ideas"] = new JsonArray(idea), ["jobs"] = new JsonArray() };
        return (board, new Idea(idea));
    }

    [Fact]
    public void Adds_a_backlog_job_numbered_into_its_heading_with_the_specs_target()
    {
        var (board, idea) = Drafting();

        var n = Backlog.AddJob(board, idea, ["intro", "# Spec: J-999 — Sort by name", "## Target", "- Repo: o/app"], "f.md", "feature", At);

        Assert.Equal("J-001", n.Job.Id);
        Assert.Equal(["intro", "# Spec: J-001 — Sort by name", "## Target", "- Repo: o/app"], n.Spec);
        Assert.Equal(
            """{"id":"J-001","idea":"I-001","kind":"feature","title":"Sort by name","stage":"backlog","round":1,"branch":"factory/J-001","base":null,"target":"o/app","pr":null,"reviews":{"security":"pending","code":"pending","ux":"pending","ui":"pending"},"decision":null,"gate":null,"flag":null,"merged":null,"approvedAt":null,"costUsd":null,"durationMs":null,"createdAt":"2026-11-10T12:00:00.000Z","updatedAt":"2026-11-10T12:00:00.000Z","history":[]}""",
            n.Job.Node.ToJsonString());
        Assert.Same(n.Job.Node, board["jobs"]![0]);
        Assert.Equal("""["J-001"]""", idea.Node["jobs"]!.ToJsonString());
        Assert.Equal("""{"ts":"2026-11-10T12:00:00.000Z","job":"J-001","kind":"stage","from":null,"to":"backlog","by":"orchestrator","idea":"I-001"}""", n.Event.ToJsonString());
    }

    [Fact]
    public void A_stacked_idea_stacks_its_jobs_and_a_spec_with_no_target_has_none()
    {
        var (board, idea) = Drafting("J-007");
        board["seq"] = new JsonObject { ["J"] = 7 };

        var n = Backlog.AddJob(board, idea, ["#Spec:Plain"], "f.md", "feature", At);

        Assert.Equal(("J-008", "J-007", null, "Plain"), (n.Job.Id, n.Job.Base, n.Job.Target, (string?)n.Job.Node["title"]));
    }

    [Theory]
    [InlineData(new[] { "no heading" }, "no '# Spec:' heading in f.md")]
    [InlineData(new[] { "# Spec:   " }, "empty title in f.md")]
    public void Refuses_a_spec_with_no_heading_or_title(string[] lines, string refusal)
    {
        var (board, idea) = Drafting();

        Assert.Equal(refusal, Assert.Throws<FactoryException>(() => Backlog.AddJob(board, idea, lines, "f.md", "feature", At)).Message);
        Assert.Empty(board["jobs"]!.AsArray());
    }
}
