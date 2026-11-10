using System.Text.Json.Nodes;
using Factory.Core;

namespace Factory.UnitTests;

/// <summary>Core's pieces behind spec 04's commands: Get-Content/Set-Content lines, rework's Scope +, purge, seq and scaffolds.</summary>
public class BoardCommandRulesTests
{
    [Theory]
    [InlineData("a\r\nb\n", new[] { "a", "b" })]
    [InlineData("a\rb", new[] { "a", "b" })]
    [InlineData("a\n\nb\n\n", new[] { "a", "", "b", "" })]
    [InlineData("", new string[0])]
    [InlineData(null, new string[0])]
    public void Lines_split_as_Get_Content_does(string? text, string[] lines) => Assert.Equal(lines, PsText.Lines(text));

    [Fact]
    public void Content_ends_every_line_as_Set_Content_does() => Assert.Equal("a\r\nb\r\n", PsText.Content(["a", "b"], "\r\n"));

    [Fact]
    public void Scope_plus_lines_give_their_globs_trimmed_without_case()
    {
        Assert.Equal(["tests/**", "lib/*.cs", "x"], Rework.Widening("Fix it.\r\n  scope +: `tests/**` , lib/*.cs\nSCOPE +: x,,\nScope: no"));
        Assert.Empty(Rework.Widening("no scope here"));
    }

    [Fact]
    public void Widen_inserts_the_globs_after_the_scope_line_marked_with_the_round() =>
        Assert.Equal(["- In:", "  - `a`", "  - `b` (rework 2)", "- Out:"], Rework.Widen(["- In:", "  - `a`", "- Out:"], 1, ["b"], 2));

    [Fact]
    public void Update_seq_raises_seq_to_the_highest_id_and_keeps_a_higher_one()
    {
        var board = new JsonObject { ["seq"] = "not an object" };

        Assert.Equal(7, Ids.UpdateSeq(board, "J", new JsonArray(new JsonObject { ["id"] = "J-007" }, new JsonObject { ["id"] = "j-002" }, new JsonObject { ["id"] = "X-9" })));
        board["seq"]!["J"] = 12;
        Assert.Equal(12, Ids.UpdateSeq(board, "J", new JsonArray(new JsonObject { ["id"] = "J-007" })));
        Assert.Equal("J-013", Ids.Next(board, "J", []));
    }

    [Fact]
    public void Purge_plans_evaluated_finished_jobs_and_ideas_with_none_left()
    {
        var board = new JsonObject
        {
            ["jobs"] = new JsonArray(
                new JsonObject { ["id"] = "J-001", ["stage"] = "done" }, new JsonObject { ["id"] = "J-002", ["stage"] = "DISCARDED" },
                new JsonObject { ["id"] = "J-003", ["stage"] = "done" }, new JsonObject { ["id"] = "J-004", ["stage"] = "ready" }),
            ["ideas"] = new JsonArray(
                new JsonObject { ["id"] = "I-001", ["status"] = "failed", ["jobs"] = new JsonArray("J-001", "J-002") },
                new JsonObject { ["id"] = "I-002", ["status"] = "abandoned", ["jobs"] = new JsonArray("J-001", "J-004") },
                new JsonObject { ["id"] = "I-003", ["status"] = "done" },
                new JsonObject { ["id"] = "I-004", ["status"] = "waiting" }),
        };

        var plan = PurgePlan.For(board, id => id != "J-003");
        plan.Apply(board);

        Assert.Equal(["I-001", "I-003", "J-001", "J-002"], plan.Gone);
        Assert.Equal("""{"J":4,"I":4}""", board["seq"]!.ToJsonString());
        Assert.Equal(["J-003", "J-004"], board["jobs"]!.AsArray().Select(j => (string?)j!["id"]));
        Assert.Equal(["I-002", "I-004"], board["ideas"]!.AsArray().Select(i => (string?)i!["id"]));
    }

    [Theory]
    [InlineData("""{"name":"todo","resource":"Todo","db":"sqlite","ui":"none"}""", null)]
    [InlineData("""{"name":"todo","resource":"Todo","db":"postgres","ui":"react","events":true,"auth":true}""", null)]
    [InlineData("[]", "the scaffold options must be a JSON object")]
    [InlineData("""{"name":"Todo","resource":"Todo","db":"sqlite","ui":"none"}""", "scaffold name 'Todo' must be lower-case letters and digits, starting with a letter")]
    [InlineData("""{"name":"todo","resource":"todo","db":"sqlite","ui":"none"}""", "scaffold resource 'todo' must be PascalCase")]
    [InlineData("""{"name":"todo","resource":"Todo","db":"SQLite","ui":"none"}""", "scaffold db 'SQLite' must be sqlite, sqlserver or postgres")]
    [InlineData("""{"name":"todo","resource":"Todo","db":"sqlite"}""", "scaffold ui '' must be react, blazor, blazor-maui or none")]
    [InlineData("""{"name":"todo","resource":"Todo","db":"sqlite","ui":"none","events":"yes"}""", "scaffold events must be true or false")]
    [InlineData("""{"name":"todo","resource":"Todo","db":"sqlite","ui":"none","auth":1}""", "scaffold auth must be true or false")]
    [InlineData("""{"name":"todo","resource":"Todo","db":"sqlite","ui":"none","auth":true}""", "scaffold auth needs a web front end (react, blazor or blazor-maui) for sign-in to redirect back to")]
    public void Scaffold_options_are_checked_as_Test_Scaffold_does(string json, string? error) =>
        Assert.Equal(error, Scaffold.Error(JsonNode.Parse(json)));
}
