using Factory.Cli;
using Factory.Core;
using Factory.Store.Abstractions;
using NSubstitute;

namespace Factory.UnitTests;

public class EventCommandTests : CommandTestBase
{
    private readonly IStateSettings _settings = Substitute.For<IStateSettings>();

    private EventCommand Sut => new(Paths, Files, _settings, Clock);

    [Fact]
    public void Appends_a_json_event_with_ts_first_and_the_jobs_round()
    {
        var s = FakeSession.With(Jobs.New("J-001", "build", round: 2));

        Sut.Run(Args("event", """{"job":"J-001","kind":"cost","usd":1.25}"""), s);

        Assert.Equal("""{"ts":"2026-11-10T12:00:00.000Z","job":"J-001","kind":"cost","usd":1.25,"round":2}""", s.Events.Single().ToJsonString());
        Assert.Equal(0, s.Saves);
    }

    [Fact]
    public void Keeps_a_round_the_event_gives_and_adds_none_for_an_unknown_job()
    {
        var s = FakeSession.With(Jobs.New("J-001", "build", round: 2));

        Sut.Run(Args("event", """{"job":"J-001","kind":"note","round":1}"""), s);
        Sut.Run(Args("event", """{"job":"J-009","kind":"note"}"""), s);

        Assert.Equal(1, (int)s.Events[0]["round"]!);
        Assert.False(s.Events[1].ContainsKey("round"));
    }

    [Fact]
    public void Builds_the_event_from_named_parameters()
    {
        var s = FakeSession.With();

        Sut.Run(Args("event", "-Kind", "agent-start", "-Idea", "I-001", "-Agent", "builder", "-Detail", "go"), s);

        Assert.Equal("""{"ts":"2026-11-10T12:00:00.000Z","idea":"I-001","kind":"agent-start","agent":"builder","detail":"go"}""", s.Events.Single().ToJsonString());
    }

    [Theory]
    [InlineData("""{"kind":"party"}""", "unknown event kind 'party'")]
    [InlineData("""{"job":"J-001"}""", "unknown event kind ''")]
    [InlineData("[1]", "an event must be a JSON object")]
    public void Refuses_an_unknown_kind(string json, string message)
    {
        Assert.Equal(message, Assert.Throws<FactoryException>(() => Sut.Run(Args("event", json), FakeSession.With())).Message);
    }

    [Fact]
    public void While_files_are_shared_an_agent_stop_copies_the_subagents_transcript_into_the_job()
    {
        _settings.SharesFiles.Returns(true);
        var transcript = Path.Combine(Path.GetTempPath(), "claude", "t.jsonl");
        Files.Put(transcript, "{\"type\":\"user\"}");
        Files.Put(Path.Combine(Home, "events.jsonl"),
            $"{{\"kind\":\"agent-start\",\"job\":\"J-001\",\"agent\":\"code-reviewer\",\"agentId\":\"abc\",\"transcript\":{System.Text.Json.JsonSerializer.Serialize(transcript)}}}\n");
        var s = FakeSession.With(Jobs.New("J-001", "review"));

        Sut.Run(Args("event", """{"job":"J-001","kind":"agent-stop","agent":"code-reviewer"}"""), s);

        Assert.Equal("{\"type\":\"user\"}", Files.Text(JobFile("J-001", "transcripts/agent-abc.jsonl")));
    }

    [Fact]
    public void On_the_local_backend_nothing_is_copied()
    {
        var s = FakeSession.With(Jobs.New("J-001", "review"));

        Sut.Run(Args("event", """{"job":"J-001","kind":"agent-stop","agent":"code-reviewer"}"""), s);

        Assert.Null(Files.Text(JobFile("J-001", "transcripts/agent-abc.jsonl")));
    }
}
