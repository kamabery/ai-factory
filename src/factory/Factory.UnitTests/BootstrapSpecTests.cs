using System.Text.Json.Nodes;
using Factory.Core;

namespace Factory.UnitTests;

public class BootstrapSpecTests
{
    private static JsonObject Sc(string json) => JsonNode.Parse(json)!.AsObject();

    [Theory]
    [InlineData("""{"name":"todo","resource":"Todo","db":"sqlite","ui":"none"}""", new[] { "dotnet new aspire-base -n todo -o . --dbProvider sqlite --resource Todo" })]
    [InlineData("""{"name":"todo","resource":"Todo","db":"sqlite","ui":"react","events":false}""",
        new[] { "dotnet new aspire-base -n todo -o . --dbProvider sqlite --resource Todo", "aspire-addon add ui-react" })]
    [InlineData("""{"name":"shop","resource":"Product","db":"postgres","ui":"blazor-maui","events":true}""",
        new[] { "dotnet new aspire-base -n shop -o . --dbProvider postgres --resource Product", "aspire-addon add ui-blazor --web true --maui true", "aspire-addon add events" })]
    [InlineData("""{"name":"a","resource":"B","db":"sqlserver","ui":"blazor","events":false,"auth":true}""",
        new[] { "dotnet new aspire-base -n a -o . --dbProvider sqlserver --resource B", "aspire-addon add ui-blazor --web true --maui false", "aspire-addon add auth" })]
    public void Commands_scaffold_then_ui_then_events_then_auth(string sc, string[] expected)
    {
        Assert.Equal(expected, Scaffold.Commands(Sc(sc)));
    }

    [Fact]
    public void Fills_the_template_with_the_options_gate_and_commands()
    {
        const string template = "# Spec: Bootstrap {{name}}\n{{resource}} {{db}} {{ui}} {{events}} {{auth}} {{templates}}\n## Target\n- Repo: {{target}}\n## Gate\n{{gate}}\n## AC\n{{commands}}\n";

        var lines = BootstrapSpec.Lines(Sc("""{"name":"todo","resource":"Todo","db":"sqlite","ui":"react","events":true}"""), "o/todo", template, "T:/tp");

        Assert.Equal(
        [
            "# Spec: Bootstrap todo",
            "Todo sqlite react true false T:/tp",
            "## Target",
            "- Repo: o/todo",
            "## Gate",
            "- build: `dotnet build todo.slnx`",
            "- test: `dotnet test todo.slnx`",
            "- apphost: `dotnet build .aspire/apphost.cs`",
            "- infra: `aspire-addon infra check`",
            "- ui: `Set-Location src/ui/react; if (-not (Test-Path node_modules/.package-lock.json) -or (Get-Item package-lock.json).LastWriteTime -gt (Get-Item -Force node_modules/.package-lock.json).LastWriteTime) { npm ci }; npm run build`",
            "## AC",
            "  - `dotnet new aspire-base -n todo -o . --dbProvider sqlite --resource Todo`",
            "  - `aspire-addon add ui-react`",
            "  - `aspire-addon add events`",
            "",
        ], lines);
    }

    [Fact]
    public void Only_a_react_ui_gates_on_npm_and_auth_reads_true_when_set()
    {
        var lines = BootstrapSpec.Lines(Sc("""{"name":"x","resource":"X","db":"sqlite","ui":"blazor","auth":true}"""), "o/x", "{{auth}}\r\n{{gate}}", "t");

        Assert.Equal("true", lines[0]);
        Assert.DoesNotContain(lines, l => l.StartsWith("- ui:", StringComparison.Ordinal));
    }
}
