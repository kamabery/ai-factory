using System.Text.Json.Nodes;
using Factory.Cli;
using Factory.Cli.Abstractions;
using Factory.Core;
using Factory.Core.Abstractions;
using Factory.Store;
using Factory.Store.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Factory.UnitTests;

/// <summary>discard and resume, ported with spec 11, and JobWork's Remove-JobWork.</summary>
public class DiscardResumeTests : CommandTestBase
{
    private readonly IJobWork _work = Substitute.For<IJobWork>();

    // --- discard ---------------------------------------------------------------------------------

    [Theory]
    [InlineData("backlog")]
    [InlineData("canary")]
    [InlineData("ready")]
    [InlineData("blocked")]
    public void Discard_moves_the_job_then_removes_its_work_and_destroys_its_preview(string stage)
    {
        var s = FakeSession.With(Jobs.New("J-001", stage));

        new DiscardCommand(_work, Clock).Run(Args("discard", "J-001"), s);

        Assert.Equal(("discarded", "human"), ((string?)s.Job("J-001")["stage"], (string?)s.Events.Single()["by"]));
        Received.InOrder(() => { _work.RemoveJobWork(s, Arg.Any<Job>()); _work.StartDestroy(Arg.Any<Job>()); });
    }

    [Fact]
    public void Discard_refuses_a_building_job_and_one_with_live_jobs_stacked_on_it()
    {
        var sut = new DiscardCommand(_work, Clock);
        Assert.Equal("discard refused: J-001 is in 'build', needs backlog|canary|ready|blocked",
            Assert.Throws<FactoryException>(() => sut.Run(Args("discard", "J-001"), FakeSession.With(Jobs.New("J-001", "build")))).Message);

        var s = FakeSession.With(Jobs.New("J-001", "ready"), Jobs.New("J-002", "build").With("base", "J-001"), Jobs.New("J-003", "done").With("base", "J-001"));
        Assert.Equal("discard refused: J-002 stacked on J-001; discard them first, or merge J-001",
            Assert.Throws<FactoryException>(() => sut.Run(Args("discard", "J-001"), s)).Message);
        Assert.Equal(0, s.Saves);
        _work.DidNotReceiveWithAnyArgs().RemoveJobWork(default!, default!);
    }

    // --- resume ----------------------------------------------------------------------------------

    [Theory]
    [InlineData("ready")]
    [InlineData("blocked")]
    public void Resume_queues_a_usage_limited_job_to_continue_its_session_in_the_stage_it_stopped_in(string stage)
    {
        var job = Jobs.New("J-001", stage).With("flag", new JsonObject { ["reason"] = "usage-limit", ["detail"] = "x", ["stage"] = "review", ["session"] = "sess-1" });
        var s = FakeSession.With(job);

        new ResumeCommand(Clock).Run(Args("resume", "J-001"), s);

        var j = s.Job("J-001");
        Assert.Equal(("approved", "review", "sess-1", Now, null), ((string?)j["stage"], (string?)j["resume"], (string?)j["resumeSession"], (string?)j["approvedAt"], j["flag"]));
        Assert.Equal("human", (string?)s.Events.Single()["by"]);
    }

    [Fact]
    public void Resume_refuses_outside_ready_or_blocked_and_a_job_not_flagged_usage_limit()
    {
        var sut = new ResumeCommand(Clock);
        Assert.Equal("resume refused: J-001 is in 'build', needs ready|blocked",
            Assert.Throws<FactoryException>(() => sut.Run(Args("resume", "J-001"), FakeSession.With(Jobs.New("J-001", "build")))).Message);
        var s = FakeSession.With(Jobs.New("J-001", "ready").With("flag", new JsonObject { ["reason"] = "escalated" }));
        Assert.Equal("resume refused: J-001 is not flagged usage-limit (rework, redeploy or discard it)",
            Assert.Throws<FactoryException>(() => sut.Run(Args("resume", "J-001"), s)).Message);
        Assert.Equal("resume refused: J-001 is not flagged usage-limit (rework, redeploy or discard it)",
            Assert.Throws<FactoryException>(() => sut.Run(Args("resume", "J-001"), FakeSession.With(Jobs.New("J-001", "ready")))).Message);
        Assert.Equal(0, s.Saves);
    }

    // --- Remove-JobWork --------------------------------------------------------------------------

    private readonly IGitHub _gh = Substitute.For<IGitHub>();

    private JobWork Sut => new(Paths, Files, new FakeGit(), _gh, Substitute.For<IGateShell>(), Substitute.For<IFactoryLauncher>(), Options.Create(new FactoryOptions()), Clock);

    [Fact]
    public void Remove_job_work_closes_the_open_PR_deletes_the_branch_and_the_work_dir()
    {
        _gh.OpenPr("o/r", "factory/J-001").Returns(new PullRequest(4, "u"));
        var wt = Path.Combine(Work, "jobs", "J-001");
        Files.Put(Path.Combine(wt, "a.txt"), "x");
        var s = FakeSession.With(Jobs.New("J-001", "discarded"));

        Sut.RemoveJobWork(s, new Job(s.Job("J-001")));

        _gh.Received(1).Send(HttpMethod.Patch, "/repos/o/r/pulls/4", Arg.Is<JsonNode?>(b => b!.ToJsonString() == """{"state":"closed"}"""));
        _gh.Received(1).Send(HttpMethod.Delete, "/repos/o/r/git/refs/heads/factory/J-001", null);
        Assert.False(Files.DirectoryExists(wt));
    }

    [Fact]
    public void Remove_job_work_without_an_open_PR_only_deletes_the_branch_and_with_no_target_touches_no_GitHub()
    {
        var s = FakeSession.With(Jobs.New("J-001", "discarded"), Jobs.New("J-002", "discarded", target: null));

        Sut.RemoveJobWork(s, new Job(s.Job("J-001")));
        _gh.DidNotReceive().Send(HttpMethod.Patch, Arg.Any<string>(), Arg.Any<JsonNode?>());
        _gh.Received(1).Send(HttpMethod.Delete, "/repos/o/r/git/refs/heads/factory/J-001", null);

        _gh.ClearReceivedCalls();
        Sut.RemoveJobWork(s, new Job(s.Job("J-002")));
        Assert.Empty(_gh.ReceivedCalls());
    }
}
