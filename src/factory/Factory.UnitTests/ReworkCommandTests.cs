using System.Text.Json.Nodes;
using Factory.Cli;
using Factory.Core;

namespace Factory.UnitTests;

public class ReworkCommandTests : CommandTestBase
{
    private static readonly string Nl = Environment.NewLine;

    private ReworkCommand Sut => new(Paths, Files, Clock);

    private static JsonObject Ready() => Jobs.New("J-001", "ready", round: 2)
        .With("flag", new JsonObject { ["reason"] = "max-rounds", ["detail"] = "x" })
        .With("decision", "ESCALATE")
        .With("baseMoved", new JsonObject { ["by"] = "J-002" })
        .With("deploy", new JsonObject { ["status"] = "up", ["attempts"] = 3 });

    [Fact]
    public void Queues_another_round_with_the_note_and_a_fresh_start()
    {
        var s = FakeSession.With(Ready());

        Sut.Run(Args("rework", "J-001", "fix the tests"), s);

        var j = s.Job("J-001");
        Assert.Equal(("approved", 3, null, Now), ((string?)j["stage"], (int)j["round"]!, (string?)j["decision"], (string?)j["approvedAt"]));
        Assert.Null(j["flag"]);
        Assert.Null(j["baseMoved"]);
        Assert.True(j.ContainsKey("baseMoved"));
        Assert.Equal((0, 0), ((int)j["deploy"]!["attempts"]!, (int)j["deployFixes"]!));
        Assert.Equal("deployFixes", j.Last().Key);   // a new key goes last, as on factory.ps1's ordered hashtable
        Assert.Equal("pending", (string?)j["reviews"]!["code"]);
        Assert.Equal("fix the tests" + Nl, Files.Text(JobFile("J-001", "rework-3.md")));
        Assert.Equal(["stage"], s.Kinds);
    }

    [Fact]
    public void Scope_plus_lines_widen_the_specs_Scope_In_and_note_it()
    {
        var s = FakeSession.With(Ready());
        Files.Put(JobFile("J-001", "spec.md"), "# Spec\n\n## Scope\n- In:\n  - `src/**`\n- Out: docs\n");

        Sut.Run(Args("rework", "J-001", "Also the tests.\nscope +: `tests/**`, lib/*.cs\n"), s);

        Assert.Equal(string.Join(Nl, "# Spec", "", "## Scope", "- In:", "  - `src/**`", "  - `tests/**` (rework 3)", "  - `lib/*.cs` (rework 3)", "- Out: docs") + Nl,
            Files.Text(JobFile("J-001", "spec.md")));
        Assert.Equal(["note", "stage"], s.Kinds);
        Assert.Equal("scope widened (rework 3): tests/**, lib/*.cs", (string?)s.Events[0]["detail"]);
    }

    [Fact]
    public void Refuses_to_widen_a_spec_that_is_not_scope_checked()
    {
        var s = FakeSession.With(Ready());
        Files.Put(JobFile("J-001", "spec.md"), "# Spec\n");

        var e = Assert.Throws<FactoryException>(() => Sut.Run(Args("rework", "J-001", "Scope +: a/**"), s));

        Assert.Equal("rework refused: Scope + widens the spec's Scope: In globs, and this spec has none (it is not scope-checked)", e.Message);
        Assert.Equal((0, 2), (s.Saves, (int)s.Job("J-001")["round"]!));
    }

    [Fact]
    public void Needs_a_note_and_a_ready_or_blocked_job()
    {
        Assert.Equal("usage: rework <id> <note>",
            Assert.Throws<FactoryException>(() => Sut.Run(Args("rework", "J-001"), FakeSession.With(Ready()))).Message);
        Assert.Equal("rework refused: J-001 is in 'build', needs ready|blocked",
            Assert.Throws<FactoryException>(() => Sut.Run(Args("rework", "J-001", "n"), FakeSession.With(Jobs.New("J-001", "build")))).Message);
    }
}
