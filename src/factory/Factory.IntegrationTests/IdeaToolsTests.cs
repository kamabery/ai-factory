using System.Text.Json.Nodes;
using Factory.Cli;
using Factory.Store;
using Microsoft.Extensions.Options;

namespace Factory.IntegrationTests;

/// <summary>The idea tools on a real disk: the fence, the read tools, and the spec tools' files.</summary>
public sealed class IdeaToolsTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly IdeaFiles _files;
    private readonly string _target;

    public IdeaToolsTests()
    {
        var paths = new FactoryPaths(Options.Create(new FactoryOptions { Home = _dir["state"], Scripts = FactoryPaths.FindScripts(AppContext.BaseDirectory) }),
            new GitClient(new ProcessRunner()));
        _files = new IdeaFiles(paths, new FileStore());
        _target = Directory.CreateDirectory(_dir["target"]).FullName;
        Directory.CreateDirectory(Path.Combine(_target, "docs"));
        Directory.CreateDirectory(Path.Combine(_target, "node_modules"));
        File.WriteAllText(Path.Combine(_target, "README.md"), "the readme");
        File.WriteAllText(Path.Combine(_target, "docs", "DESIGN.md"), "# Design\nUse the Button component.");
        File.WriteAllText(Path.Combine(_target, "node_modules", "x.js"), "Button");
        File.WriteAllText(_dir["outside.txt"], "secret");
        Session("""{"id":"S-001","target":"a/b","checkout":"CHECKOUT","idea":null,"specs":[]}""");
    }

    public void Dispose() => _dir.Dispose();

    private void Session(string json) =>
        _files.SaveSession("S-001", JsonNode.Parse(json.Replace("CHECKOUT", _target.Replace("\\", "\\\\")))!.AsObject());

    private ToolAnswer Call(string name, string input) => new IdeaTools(_files, "S-001").Call(name, JsonNode.Parse(input)!.AsObject());

    private static string Json(string s) => System.Text.Json.JsonSerializer.Serialize(s);

    [Fact]
    public void Reads_inside_the_target_and_the_templates_and_nothing_else()
    {
        Assert.Equal(new ToolAnswer("the readme"), Call("read_file", """{"path":"README.md"}"""));
        Assert.Contains("# Spec:", Call("read_file", $$"""{"path":{{Json(Path.Combine(_files.TemplatesDir, "spec.md"))}}}""").Text);
        List<string> outside = [Json("../outside.txt"), Json(_dir["outside.txt"])];
        // Only Windows reads a backslash as a separator; elsewhere `..\outside.txt` names a file inside the target.
        if (OperatingSystem.IsWindows()) outside.Add(Json(@"..\outside.txt"));
        foreach (var bad in outside)
        {
            var r = Call("read_file", $$"""{"path":{{bad}}}""");
            Assert.True(r.IsError);
            Assert.DoesNotContain("secret", r.Text);
            Assert.Contains("is outside the target and factory/templates", r.Text);
        }
    }

    [Fact]
    public void A_missing_or_large_file_is_an_error()
    {
        File.WriteAllText(Path.Combine(_target, "big.txt"), new string('x', 257 * 1024));

        Assert.Equal(new ToolAnswer("no file 'nope.md'", true), Call("read_file", """{"path":"nope.md"}"""));
        Assert.Equal(new ToolAnswer("'big.txt' is larger than 256 KB; use grep", true), Call("read_file", """{"path":"big.txt"}"""));
    }

    [Fact]
    public void List_dir_shows_folders_first_and_skips_node_modules_and_the_like()
    {
        Assert.Equal(new ToolAnswer("docs/\nREADME.md"), Call("list_dir", "{}"));
        Assert.Equal(new ToolAnswer("no folder 'nope'", true), Call("list_dir", """{"path":"nope"}"""));
    }

    [Fact]
    public void Grep_ignores_case_skips_node_modules_filters_by_glob_and_caps_its_hits()
    {
        Assert.Equal(new ToolAnswer("docs/DESIGN.md:2: Use the Button component."), Call("grep", """{"pattern":"button"}"""));
        Assert.Equal(new ToolAnswer("no matches"), Call("grep", """{"pattern":"button","glob":"*.txt"}"""));
        File.WriteAllLines(Path.Combine(_target, "many.txt"), Enumerable.Repeat("hit", 250));
        var many = Call("grep", """{"pattern":"hit","glob":"many.*"}""").Text.Split('\n');
        Assert.Equal(201, many.Length);
        Assert.Equal("(more matches not shown; narrow the search)", many[^1]);
    }

    [Fact]
    public void Submit_specs_waits_for_the_brief_then_writes_each_spec_and_the_sessions_list()
    {
        var spec = Json("# Spec: Add a todo\n\nbody");
        Assert.Equal(new ToolAnswer("the brief is not confirmed yet; submit_specs comes after the human confirms it", true),
            Call("submit_specs", $$"""{"specs":[{"k":1,"markdown":{{spec}}}]}"""));

        Session("""{"id":"S-001","target":"a/b","checkout":"CHECKOUT","idea":"I-001","specs":[]}""");
        Assert.Equal(new ToolAnswer("submit 1 to 5 specs", true), Call("submit_specs", """{"specs":[]}"""));
        Assert.Equal(new ToolAnswer("spec 1 has no '# Spec: <title>' line", true), Call("submit_specs", """{"specs":[{"k":1,"markdown":"no title"}]}"""));

        Assert.Equal(new ToolAnswer("wrote 2 spec(s); the human now decides each one in the panel"),
            Call("submit_specs", $$"""{"specs":[{"k":1,"markdown":{{spec}}},{"k":2,"markdown":{{Json("# Spec: Check off\n")}}}]}"""));
        Assert.Equal("# Spec: Add a todo\n\nbody" + Environment.NewLine, File.ReadAllText(_dir[Path.Combine("state", "ideas", "I-001", "spec-1.md")]));
        Assert.Equal("""[{"k":1,"title":"Add a todo","decision":null,"job":null},{"k":2,"title":"Check off","decision":null,"job":null}]""",
            _files.Session("S-001")!["specs"]!.ToJsonString());
    }

    [Fact]
    public void Once_a_spec_is_decided_submit_specs_is_refused_and_only_an_undecided_spec_can_be_revised()
    {
        Session("""{"id":"S-001","checkout":"CHECKOUT","idea":"I-001","specs":[{"k":1,"title":"a","decision":"approved"},{"k":2,"title":"b","decision":null}]}""");

        Assert.Equal(new ToolAnswer("the human has started deciding specs; use revise_spec", true), Call("submit_specs", """{"specs":[{"k":1,"markdown":"# Spec: x"}]}"""));
        Assert.Equal(new ToolAnswer("spec 1 is already decided (approved)", true), Call("revise_spec", """{"k":1,"markdown":"# Spec: x"}"""));
        Assert.Equal(new ToolAnswer("there is no spec 3", true), Call("revise_spec", """{"k":3,"markdown":"# Spec: x"}"""));
        Assert.Equal(new ToolAnswer("the new markdown has no '# Spec: <title>' line", true), Call("revise_spec", """{"k":2,"markdown":"x"}"""));
        Assert.Equal(new ToolAnswer("spec 2 revised"), Call("revise_spec", """{"k":2,"markdown":"# Spec: Better b"}"""));
        Assert.Equal("Better b", (string?)_files.Session("S-001")!["specs"]![1]!["title"]);
        Assert.StartsWith("# Spec: Better b", File.ReadAllText(_dir[Path.Combine("state", "ideas", "I-001", "spec-2.md")]));
    }

    [Fact]
    public void With_no_clone_yet_only_the_templates_can_be_read()
    {
        Session("""{"id":"S-001","target":null,"checkout":null}""");

        Assert.Contains("brief.md", Call("list_dir", "{}").Text);
        Assert.True(Call("read_file", """{"path":"README.md"}""").IsError);
    }
}
