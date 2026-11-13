using System.Text.Json.Nodes;
using Factory.Cli;

namespace Factory.UnitTests;

public class IdeaPromptTests
{
    private static JsonArray Chat(string json) => JsonNode.Parse(json)!.AsArray();

    [Fact]
    public void The_first_turn_sends_every_user_text_so_far()
    {
        var chat = Chat("""[{"role":"user","content":[{"type":"text","text":"Target: a/b\n\nIdea: tags"}]},{"role":"user","content":[{"type":"text","text":"more"}]}]""");

        Assert.Equal("Target: a/b\n\nIdea: tags\n\nmore", IdeaPrompt.Turn(chat, first: true));
    }

    [Fact]
    public void A_later_turn_sends_only_what_came_after_the_last_assistant_message_with_a_ui_answer_spelled_out()
    {
        var chat = Chat("""
            [{"role":"user","content":[{"type":"text","text":"old"}]},
             {"role":"assistant","content":[{"type":"tool_use","id":"r1","name":"read_file","input":{}},{"type":"tool_use","id":"q1","name":"ask_questions","input":{}}]},
             {"role":"user","content":[{"type":"tool_result","tool_use_id":"r1","content":"the readme"},{"type":"tool_result","tool_use_id":"q1","content":"Just me"},{"type":"text","text":"and soon"}]}]
            """);

        Assert.Equal("The human answered ask_questions: Just me\n\nand soon", IdeaPrompt.Turn(chat, first: false));
    }

    [Fact]
    public void A_retry_with_nothing_new_is_continue()
    {
        var chat = Chat("""[{"role":"user","content":[{"type":"text","text":"x"}]},{"role":"assistant","content":[{"type":"text","text":"y"}]}]""");

        Assert.Equal("Continue.", IdeaPrompt.Turn(chat, first: false));
    }

    [Fact]
    public void A_plain_string_content_is_text_too()
    {
        Assert.Equal("hello", IdeaPrompt.Turn(Chat("""[{"role":"user","content":"hello"}]"""), first: true));
    }

    [Fact]
    public void A_tool_results_text_is_its_string_or_its_text_blocks()
    {
        Assert.Equal("a", IdeaPrompt.ResultText(JsonValue.Create("a")));
        Assert.Equal("a\nb", IdeaPrompt.ResultText(JsonNode.Parse("""[{"type":"text","text":"a"},{"type":"image"},{"type":"text","text":"b"}]""")));
    }

    [Fact]
    public void The_system_prompt_is_the_preamble_the_grill_the_spec_writer_and_both_templates_without_front_matter()
    {
        var root = FactoryRoot();

        var system = IdeaPrompt.System(root);

        Assert.StartsWith("# The factory's idea session, in the dashboard", system);
        foreach (var part in new[] { "# The grill skill", "# The spec-writer's rules", "# The brief template (factory/templates/brief.md)", "# The spec template (factory/templates/spec.md)" })
            Assert.Contains(part, system);
        Assert.DoesNotContain("\nname: grill", system);
    }

    private static string FactoryRoot() => Path.GetFullPath(Path.Combine(Factory.Store.FactoryPaths.FindScripts(AppContext.BaseDirectory)!, "..", ".."));
}
