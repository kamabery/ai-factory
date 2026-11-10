using Factory.Cli;
using Factory.Core;

namespace Factory.UnitTests;

public class ApproveCommandTests : CommandTestBase
{
    private const string Spec = "# Spec: J-001 — Thing\n\n## Target\n- Repo: o/r\n\n## Gate\n- build: `exit 0`\n";

    private ApproveCommand Sut => new(Paths, Files, Clock);

    [Fact]
    public void Approves_a_backlog_spec_taking_its_target_and_the_approval_time()
    {
        var s = FakeSession.With(Jobs.New("J-001", "backlog", target: null));
        Files.Put(JobFile("J-001", "spec.md"), Spec.Replace("o/r", "owner/name"));

        var r = Sut.Run(Args("approve", "J-001"), s);

        Assert.Empty(r.Out);
        var j = s.Job("J-001");
        Assert.Equal(("approved", "owner/name", Now), ((string?)j["stage"], (string?)j["target"], (string?)j["approvedAt"]));
        Assert.Equal(1, s.Saves);
        Assert.Equal("{\"ts\":\"2026-11-10T12:00:00.000Z\",\"job\":\"J-001\",\"kind\":\"stage\",\"from\":\"backlog\",\"to\":\"approved\",\"by\":\"human\"}",
            s.Events.Single().ToJsonString());
    }

    [Theory]
    [InlineData("# Spec: x\n", "approve refused: the spec has no '## Target' with '- Repo: <owner/name>'")]
    [InlineData("# Spec: x\n## Target\n- Repo: not a repo\n## Gate\n- build: `x`\n", "approve refused: target 'not a repo' is not a GitHub repo written owner/name")]
    [InlineData("# Spec: x\n## Target\n- Repo: o/r\n", "approve refused: the spec's '## Gate' has no '- <name>: `<command>`' lines")]
    public void Refuses_a_spec_without_a_target_or_a_gate_and_changes_nothing(string spec, string message)
    {
        var s = FakeSession.With(Jobs.New("J-001", "backlog"));
        Files.Put(JobFile("J-001", "spec.md"), spec);

        Assert.Equal(message, Assert.Throws<FactoryException>(() => Sut.Run(Args("approve", "J-001"), s)).Message);
        Assert.Equal((0, "backlog"), (s.Saves, (string?)s.Job("J-001")["stage"]));
    }

    [Fact]
    public void Refuses_a_job_not_in_backlog_or_without_a_spec()
    {
        Assert.Equal("approve refused: J-001 is in 'build', needs backlog",
            Assert.Throws<FactoryException>(() => Sut.Run(Args("approve", "J-001"), FakeSession.With(Jobs.New("J-001", "build")))).Message);
        Assert.Equal("J-001 has no spec.md",
            Assert.Throws<FactoryException>(() => Sut.Run(Args("approve", "J-001"), FakeSession.With(Jobs.New("J-001", "backlog")))).Message);
        Assert.Equal("no job J-009",
            Assert.Throws<FactoryException>(() => Sut.Run(Args("approve", "J-009"), FakeSession.With())).Message);
    }
}
