using System.Text.Json.Nodes;
using Factory.Cli;
using Factory.Cli.Abstractions;
using Factory.Core;
using Factory.Core.Abstractions;
using Factory.Store.Abstractions;
using NSubstitute;

namespace Factory.UnitTests;

public class MergeCommandTests : CommandTestBase
{
    private readonly FakeGit _git = new();
    private readonly IGitHub _gh = Substitute.For<IGitHub>();
    private readonly IJobWork _work = Substitute.For<IJobWork>();
    private static readonly string Wt = Path.Combine(Work, "jobs", "J-001");

    public MergeCommandTests()
    {
        _work.Fetch(Arg.Any<string>(), Arg.Any<string[]>()).Returns(new GitResult(0, ""));
        // The helpers answer null for "no error"; a substitute would answer "".
        _work.EnsureClone(Arg.Any<JsonObject>(), Arg.Any<string>()).Returns((string?)null);
        _work.Gate(Arg.Any<IBoardSession>(), Arg.Any<string>()).Returns((string?)null);
        _work.GateGreen(Arg.Any<Job>()).Returns(true);
        _work.Publish(Arg.Any<IBoardSession>(), Arg.Any<Job>()).Returns(("u4", (string?)null));
        _git.On("rev-list --count", 0, "2");
        _gh.Send(HttpMethod.Put, Arg.Any<string>(), Arg.Any<JsonNode?>()).Returns(new GitHubResponse(200, JsonNode.Parse("""{"sha":"m123"}"""), null));
    }

    private MergeCommand Sut => new(Paths, _git, _gh, _work, Clock);

    private static FakeSession Ready(params JsonObject[] more) => FakeSession.With([
        Jobs.New("J-001", "ready").With("pr", new JsonObject { ["number"] = 4, ["url"] = "u4" }).With("gate", JsonNode.Parse("""{"build":0,"sha":"g1"}""")), .. more]);

    private string Blocked(FakeSession s, string reason)
    {
        var e = Assert.Throws<FactoryException>(() => Sut.Run(Args("merge", "J-001"), s));
        Assert.Equal(("ready", reason), ((string?)s.Job("J-001")["stage"], (string?)s.Job("J-001")["flag"]!["reason"]));
        _gh.DidNotReceive().Send(HttpMethod.Put, Arg.Any<string>(), Arg.Any<JsonNode?>());
        _work.DidNotReceiveWithAnyArgs().CompleteMerge(default!, default!, default!);
        return e.Message;
    }

    [Fact]
    public void A_green_clean_branch_is_published_and_merged_on_GitHub_at_the_gated_sha()
    {
        var s = Ready();
        _work.CompleteMerge(s, Arg.Any<Job>(), "m123").Returns(["released I-001"]);

        Assert.Equal(["released I-001"], Sut.Run(Args("merge", "J-001"), s).Out);

        _work.Received(1).Fetch(Wt);
        Assert.Equal("-c core.quotepath=false -c core.safecrlf=false diff --name-only HEAD", _git.Calls[0].Args);
        Assert.Equal("rev-list --count origin/main..factory/J-001", _git.Calls[1].Args);
        _work.DidNotReceive().Gate(Arg.Any<IBoardSession>(), Arg.Any<string>());
        _gh.Received(1).Send(HttpMethod.Put, "/repos/o/r/pulls/4/merge", Arg.Is<JsonNode?>(b =>
            b!.ToJsonString() == """{"merge_method":"merge","sha":"g1","commit_title":"Merge factory/J-001: Thing"}"""));
        _gh.DidNotReceive().Send(HttpMethod.Patch, Arg.Any<string>(), Arg.Any<JsonNode?>());
    }

    [Fact]
    public void A_PR_merged_on_GitHub_already_is_recorded_without_merging_again()
    {
        var s = Ready();
        var pr = new JsonObject { ["merged"] = true };
        _work.GitHubMerge(Arg.Any<Job>()).Returns(pr);
        _work.CompleteGitHubMerge(s, Arg.Any<Job>(), pr).Returns([]);

        Assert.Equal(0, Sut.Run(Args("merge", "J-001"), s).ExitCode);

        _work.Received(1).CompleteGitHubMerge(s, Arg.Any<Job>(), pr);
        _work.DidNotReceiveWithAnyArgs().EnsureClone(default!, default!);
    }

    [Fact]
    public void A_stacked_job_retargets_its_PR_to_main_before_the_merge()
    {
        var s = FakeSession.With(Jobs.New("J-009", "done"), Jobs.New("J-001", "ready").With("base", "J-009")
            .With("pr", new JsonObject { ["number"] = 4 }).With("gate", JsonNode.Parse("""{"sha":"g1"}""")));
        _gh.Send(HttpMethod.Patch, "/repos/o/r/pulls/4", Arg.Any<JsonNode?>()).Returns(new GitHubResponse(200, null, null));

        Sut.Run(Args("merge", "J-001"), s);

        _gh.Received(1).Send(HttpMethod.Patch, "/repos/o/r/pulls/4", Arg.Is<JsonNode?>(b => b!.ToJsonString() == """{"base":"main"}"""));
        _work.Received(1).CompleteMerge(s, Arg.Any<Job>(), "m123");
    }

    [Fact]
    public void Refuses_outside_ready_and_a_job_stacked_on_an_unmerged_base()
    {
        Assert.Equal("merge refused: J-001 is in 'gate', needs ready",
            Assert.Throws<FactoryException>(() => Sut.Run(Args("merge", "J-001"), FakeSession.With(Jobs.New("J-001", "gate")))).Message);
        Assert.Equal("merge refused: J-001 is stacked on J-009; merge J-009 first",
            Assert.Throws<FactoryException>(() => Sut.Run(Args("merge", "J-001"), FakeSession.With(Jobs.New("J-009", "ready"), Jobs.New("J-001", "ready").With("base", "J-009")))).Message);
    }

    [Fact]
    public void A_clone_or_fetch_that_fails_blocks_the_merge()
    {
        _work.EnsureClone(Arg.Any<JsonObject>(), "J-001").Returns("clone of o/r failed: x");
        Assert.Equal("merge blocked: clone of o/r failed: x", Blocked(Ready(), "merge-blocked"));

        _work.EnsureClone(Arg.Any<JsonObject>(), "J-001").Returns((string?)null);
        _work.Fetch(Wt).Returns(new GitResult(1, "timeout"));
        var s = Ready();
        Assert.Equal("merge blocked: fetch failed", Blocked(s, "merge-blocked"));
        Assert.Equal("fetch failed: timeout", (string?)s.Job("J-001")["flag"]!["detail"]);
    }

    [Fact]
    public void Uncommitted_changes_or_nothing_to_merge_block_the_merge()
    {
        _git.On("diff --name-only HEAD", 0, "README.md\nsrc/a.cs");
        var s = Ready();
        Assert.Equal("merge blocked: uncommitted changes", Blocked(s, "merge-blocked"));
        Assert.Equal("uncommitted changes in the work dir: README.md, src/a.cs", (string?)s.Job("J-001")["flag"]!["detail"]);
        Assert.Empty(_git.Ran("rev-list"));

        _git.On("diff --name-only HEAD", 0, "").On("rev-list --count", 0, "0");
        Assert.Equal("merge blocked: nothing to merge", Blocked(Ready(), "merge-blocked"));
    }

    [Fact]
    public void Without_a_green_gate_at_the_head_the_gate_runs_first()
    {
        _work.GateGreen(Arg.Any<Job>()).Returns(false, true);
        var s = Ready();

        Sut.Run(Args("merge", "J-001"), s);

        _work.Received(1).Gate(s, "J-001");
        _work.Received(1).CompleteMerge(s, Arg.Any<Job>(), "m123");
    }

    [Fact]
    public void A_gate_that_cannot_run_or_runs_red_blocks_the_merge()
    {
        _work.GateGreen(Arg.Any<Job>()).Returns(false);
        _work.Gate(Arg.Any<IBoardSession>(), "J-001").Returns("no gate commands in the spec");
        Assert.Equal("merge blocked: no gate commands in the spec", Blocked(Ready(), "merge-blocked"));

        _work.Gate(Arg.Any<IBoardSession>(), "J-001").Returns((string?)null);
        var s = Ready();
        Assert.Equal("merge refused: gate red", Blocked(s, "gate-failed"));
        Assert.Equal("gate", (string?)s.Events.Single()["by"]);
    }

    [Fact]
    public void A_failed_publish_retarget_or_GitHub_merge_blocks_the_merge_with_GitHubs_message()
    {
        _work.Publish(Arg.Any<IBoardSession>(), Arg.Any<Job>()).Returns(((string?)null, "push failed: x"));
        Assert.Equal("merge blocked: push failed: x", Blocked(Ready(), "merge-blocked"));

        _work.Publish(Arg.Any<IBoardSession>(), Arg.Any<Job>()).Returns(("u4", (string?)null));
        _gh.Send(HttpMethod.Patch, Arg.Any<string>(), Arg.Any<JsonNode?>()).Returns(new GitHubResponse(422, JsonNode.Parse("""{"message":"no base"}"""), null));
        Assert.Equal("merge blocked: GitHub 422: no base", Blocked(FakeSession.With(Jobs.New("J-009", "done"), Jobs.New("J-001", "ready").With("base", "J-009").With("pr", new JsonObject { ["number"] = 4 })), "merge-blocked"));

        _gh.Send(HttpMethod.Put, Arg.Any<string>(), Arg.Any<JsonNode?>()).Returns(new GitHubResponse(405, JsonNode.Parse("""{"message":"Pull Request is not mergeable"}"""), null));
        var e = Assert.Throws<FactoryException>(() => Sut.Run(Args("merge", "J-001"), Ready()));
        Assert.Equal("merge blocked: GitHub 405: Pull Request is not mergeable", e.Message);
    }
}
