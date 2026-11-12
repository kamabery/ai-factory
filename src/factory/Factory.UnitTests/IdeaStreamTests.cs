using System.Text.Json.Nodes;
using Factory.Cli;

namespace Factory.UnitTests;

public class IdeaStreamTests
{
    private readonly List<string> _lines = [];
    private readonly IdeaStream _stream;

    public IdeaStreamTests() => _stream = new IdeaStream(l => _lines.Add(l.ToJsonString()));

    private void Feed(params string[] lines) { foreach (var l in lines) _stream.Accept(l); }

    private static string Assistant(string id, string block) => $$$"""{"type":"assistant","message":{"id":"{{{id}}}","role":"assistant","content":[{{{block}}}]}}""";

    [Fact]
    public void Streams_text_deltas_and_each_server_tool_call_but_not_ui_tools()
    {
        Feed("""{"type":"stream_event","event":{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"Hi"}}}""",
            """{"type":"stream_event","event":{"type":"content_block_delta","index":1,"delta":{"type":"input_json_delta","partial_json":"{"}}}""",
            Assistant("m1", """{"type":"tool_use","id":"r1","name":"mcp__factory__grep","input":{"pattern":" Button "}}"""),
            Assistant("m2", """{"type":"tool_use","id":"q1","name":"mcp__factory__ask_questions","input":{}}"""),
            "not json", "");

        Assert.Equal(["""{"type":"text","text":"Hi"}""", """{"type":"tool","name":"grep","summary":"Button"}"""], _lines);
    }

    [Fact]
    public void Keeps_the_turn_in_the_messages_shape_one_message_per_api_message_with_bare_tool_names()
    {
        Feed(Assistant("m1", """{"type":"thinking","thinking":"","signature":"s"}"""),
            Assistant("m1", """{"type":"tool_use","id":"r1","name":"mcp__factory__read_file","input":{"path":"README.md"}}"""),
            """{"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"r1","content":[{"type":"text","text":"readme"}],"is_error":false}]}}""",
            Assistant("m2", """{"type":"text","text":"Done."}"""));

        var messages = _stream.Finish(new JsonObject());

        Assert.Equal(3, messages.Count);
        Assert.Equal(["thinking", "tool_use"], messages[0]["content"]!.AsArray().Select(b => (string)b!["type"]!));
        Assert.Equal("read_file", (string)messages[0]["content"]![1]!["name"]!);
        Assert.Equal("""{"role":"user","content":[{"type":"tool_result","tool_use_id":"r1","content":"readme"}]}""", messages[1].ToJsonString());
        Assert.Equal("Done.", (string)messages[2]["content"]![0]!["text"]!);
    }

    [Fact]
    public void Results_claude_sends_one_by_one_join_one_user_message_and_an_error_is_marked()
    {
        Feed(Assistant("m1", """{"type":"tool_use","id":"a","name":"mcp__factory__list_dir","input":{}}"""),
            """{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"a","content":"x","is_error":true}]}}""",
            """{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"b","content":"y"}]}}""",
            """{"type":"user","message":{"content":[{"type":"text","text":"ignored"}]}}""");

        var messages = _stream.Finish(new JsonObject());

        Assert.Equal("""[{"type":"tool_result","tool_use_id":"a","content":"x","is_error":true},{"type":"tool_result","tool_use_id":"b","content":"y"}]""",
            messages[1]["content"]!.ToJsonString());
    }

    [Fact]
    public void A_pending_ui_call_gets_its_tool_use_id_its_placeholder_result_goes_and_the_server_results_beside_it_wait_for_the_answer()
    {
        Feed(Assistant("m1", """{"type":"tool_use","id":"r1","name":"mcp__factory__read_file","input":{}}"""),
            Assistant("m1", """{"type":"tool_use","id":"q1","name":"mcp__factory__ask_questions","input":{"questions":[]}}"""),
            """{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"r1","content":"readme"},{"type":"tool_result","tool_use_id":"q1","content":"Shown to the human."}]}}""");
        var session = JsonNode.Parse("""{"pending":{"id":null,"name":"ask_questions","input":{}}}""")!.AsObject();

        var messages = _stream.Finish(session);

        Assert.Single(messages);
        Assert.Equal("q1", (string)session["pending"]!["id"]!);
        Assert.Equal("""[{"type":"tool_result","tool_use_id":"r1","content":"readme"}]""", session["pendingResults"]!.ToJsonString());
    }

    [Fact]
    public void A_pending_call_from_an_earlier_turn_is_left_alone()
    {
        Feed(Assistant("m1", """{"type":"text","text":"x"}"""));
        var session = JsonNode.Parse("""{"pending":{"id":"old","name":"submit_brief"}}""")!.AsObject();

        Assert.Single(_stream.Finish(session));
        Assert.Equal("old", (string)session["pending"]!["id"]!);
    }

    [Fact]
    public void Keeps_the_result_line()
    {
        Feed("""{"type":"result","subtype":"success","is_error":false,"total_cost_usd":0.5,"session_id":"s1"}""");

        Assert.Equal(0.5, (double)_stream.Result!["total_cost_usd"]!);
    }
}
