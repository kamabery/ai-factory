using System.Text.Json.Nodes;
using Factory.Server;

namespace Factory.UnitTests;

/// <summary>Contract, "Idea sessions (dashboard)", "The phase": worked out from session.json and the board, never stored.</summary>
public class SessionPhaseTests
{
    private static JsonObject Obj(string json) => JsonNode.Parse(json)!.AsObject();

    private static JsonObject[] Jobs(string json) => [.. JsonNode.Parse(json)!.AsArray().Select(j => j!.AsObject())];

    [Theory]
    // No idea yet: the brief.
    [InlineData("""{"status":"running"}""", "[]", """{"name":"brief"}""")]
    // A bootstrap that is not done: the scaffold, with its stage and (once published) its PR.
    [InlineData("""{"idea":"I-1","bootstrap":"J-1"}""", """[{"id":"J-1","stage":"build"}]""", """{"name":"scaffold","job":"J-1","stage":"build","pr":null}""")]
    [InlineData("""{"idea":"I-1","bootstrap":"J-1"}""", """[{"id":"J-1","stage":"ready","pr":{"number":3,"url":"u"}}]""",
        """{"name":"scaffold","job":"J-1","stage":"ready","pr":{"number":3,"url":"u"}}""")]
    // Merged, or purged off the board since: the specs, until every one is decided.
    [InlineData("""{"idea":"I-1","bootstrap":"J-1"}""", """[{"id":"J-1","stage":"done"}]""", """{"name":"specs"}""")]
    [InlineData("""{"idea":"I-1","bootstrap":"J-1"}""", "[]", """{"name":"specs"}""")]
    [InlineData("""{"idea":"I-1","specs":[{"k":1,"decision":"approved","job":"J-2"},{"k":2}]}""", """[{"id":"J-2","stage":"build"}]""", """{"name":"specs"}""")]
    // Every spec decided, every one rejected: nothing reached the board, so still the specs.
    [InlineData("""{"idea":"I-1","specs":[{"k":1,"decision":"rejected"}]}""", "[]", """{"name":"specs"}""")]
    // Every spec decided and some on the board: the build, with those jobs as the board has them.
    [InlineData("""{"idea":"I-1","bootstrap":"J-1","specs":[{"k":1,"decision":"approved","job":"J-2"},{"k":2,"decision":"rejected"},{"k":3,"decision":"backlog","job":"J-3"}]}""",
        """[{"id":"J-1","stage":"done"},{"id":"J-2","stage":"review","title":"Tag","pr":null,"deploy":{"status":"up"}},{"id":"J-3","stage":"backlog","title":"Filter"}]""",
        """{"name":"build","jobs":[{"id":"J-2","title":"Tag","stage":"review","pr":null,"deploy":{"status":"up"}},{"id":"J-3","title":"Filter","stage":"backlog","pr":null,"deploy":null}]}""")]
    // A New repo session (spec 22): grill, scaffold, review (ready to merge), merged (done, or purged since).
    [InlineData("""{"kind":"repo","status":"running"}""", "[]", """{"name":"grill"}""")]
    [InlineData("""{"kind":"repo","idea":"I-1","bootstrap":"J-1"}""", """[{"id":"J-1","stage":"build"}]""", """{"name":"scaffold","job":"J-1","stage":"build","pr":null}""")]
    [InlineData("""{"kind":"repo","idea":"I-1","bootstrap":"J-1"}""", """[{"id":"J-1","stage":"ready","pr":{"number":3,"url":"u"}}]""",
        """{"name":"review","job":"J-1","stage":"ready","pr":{"number":3,"url":"u"}}""")]
    [InlineData("""{"kind":"repo","idea":"I-1","bootstrap":"J-1"}""", """[{"id":"J-1","stage":"done"}]""", """{"name":"merged"}""")]
    [InlineData("""{"kind":"repo","idea":"I-1","bootstrap":"J-1"}""", "[]", """{"name":"merged"}""")]
    public void The_phase_follows_the_session_and_the_board(string session, string jobs, string phase) =>
        Assert.Equal(phase, SessionPhase.Of(Obj(session), Jobs(jobs)).ToJsonString());
}
