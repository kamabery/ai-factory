using System.Text.Json.Nodes;
using Factory.Cli;
using Factory.Cli.Abstractions;
using Factory.Core;
using Factory.Store;
using Factory.Store.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Factory.UnitTests;

public class IdeaCommandsTests : CommandTestBase
{
    private static readonly string Cwd = Path.Combine(Path.GetTempPath(), "cmd-cwd");
    private static readonly string Scripts = Path.Combine(Path.GetTempPath(), "repo", "factory", "scripts");
    private static readonly string Template = Path.Combine(Path.GetTempPath(), "repo", "factory", "templates", "bootstrap.md");
    private const string Scaffold = """{"name":"todo","resource":"Todo","db":"sqlite","ui":"none","events":false}""";
    private readonly IGitHub _gh = Substitute.For<IGitHub>();

    public IdeaCommandsTests()
    {
        Paths.ScriptsDir.Returns(Scripts);
        Paths.TemplatesRoot.Returns("TP");
        Files.Put(Template, "# Spec: Bootstrap {{name}}\n## Target\n- Repo: {{target}}\n");
        _gh.Lookup(Arg.Any<string>()).Returns(RepoState.Existing);
    }

    private NewIdeaCommand NewIdea => new(Paths, Files, _gh, Clock);
    private AddSpecCommand AddSpec => new(Paths, Files, Options.Create(new FactoryOptions { WorkingDirectory = Cwd }), Clock);
    private static string Nl => Environment.NewLine;

    private static FakeSession WithIdea(string status, params JsonObject[] jobs)
    {
        var s = FakeSession.With(jobs);
        s.Board["ideas"]!.AsArray().Add(new JsonObject { ["id"] = "I-001", ["text"] = "x", ["target"] = "o/r", ["status"] = status, ["jobs"] = new JsonArray() });
        return s;
    }

    private const string ApprovableSpec = "# Spec: x\n## Target\n- Repo: o/r\n## Gate\n- build: `dotnet build`\n";

    private static JsonObject Idea(FakeSession s) => s.Board["ideas"]![0]!.AsObject();

    [Fact]
    public void NewIdea_on_an_existing_repo_drafts_and_writes_idea_md()
    {
        var s = FakeSession.With();

        var r = NewIdea.Run(Args("new-idea", "find books", "-Target", "o/r"), s);

        Assert.Equal(["I-001"], r.Out);
        Assert.Equal("""{"id":"I-001","text":"find books","target":"o/r","scaffold":null,"base":null,"status":"drafting","jobs":[],"createdAt":"2026-11-10T12:00:00.000Z"}""", Idea(s).ToJsonString());
        Assert.Equal($"find books{Nl}{Nl}Target: o/r{Nl}", Files.Text(Path.Combine(Home, "ideas", "I-001", "idea.md")));
        Assert.Equal("""{"ts":"2026-11-10T12:00:00.000Z","idea":"I-001","kind":"idea","detail":"drafting"}""", s.Events.Single().ToJsonString());
        Assert.Equal(1, s.Saves);
    }

    [Fact]
    public void NewIdea_on_a_new_repo_waits_on_a_bootstrap_job_from_the_template()
    {
        _gh.Lookup("o/new").Returns(RepoState.Empty);
        var s = FakeSession.With();

        var r = NewIdea.Run(Args("new-idea", "todo app", "-Target", "o/new", "-Scaffold", Scaffold), s);

        Assert.Equal(["I-001 J-001"], r.Out);
        Assert.Equal(("waiting", "todo", "J-001"), ((string?)Idea(s)["status"], (string?)Idea(s)["scaffold"]!["name"], (string?)Idea(s)["jobs"]![0]));
        Assert.Equal(("bootstrap", "o/new"), ((string?)s.Job("J-001")["kind"], (string?)s.Job("J-001")["target"]));
        Assert.Equal($"# Spec: J-001 — Bootstrap todo{Nl}## Target{Nl}- Repo: o/new{Nl}{Nl}", Files.Text(JobFile("J-001", "spec.md")));
        Assert.Equal(["idea", "stage"], s.Kinds);
        Assert.Equal(2, s.Saves);
    }

    [Fact]
    public void NewIdea_with_a_base_stacks_the_idea()
    {
        var s = FakeSession.With(Jobs.New("J-004", "ready").With("target", "o/r").With("pr", new JsonObject { ["number"] = 4 }));

        NewIdea.Run(Args("new-idea", "more", "-Target", "o/r", "-Base", "J-004"), s);

        Assert.Equal("J-004", (string?)Idea(s)["base"]);
        Assert.EndsWith($"Target: o/r{Nl}Base: factory/J-004 (unmerged; this idea builds on it){Nl}", Files.Text(Path.Combine(Home, "ideas", "I-001", "idea.md")));
    }

    [Theory]
    [InlineData(new[] { "new-idea" }, "usage: new-idea <text> -Target <owner/name> [-Scaffold <json> [-Product <file>]] [-Base <J-NNN>] [-From <I-NNN> -Feature <heading>]")]
    [InlineData(new[] { "new-idea", "x" }, "new-idea refused: a target repo (owner/name) is required")]
    [InlineData(new[] { "new-idea", "x", "-Target", "a/b/c" }, "new-idea refused: target 'a/b/c' is not a GitHub repo written owner/name")]
    [InlineData(new[] { "new-idea", "x", "-Target", "o/r", "-Scaffold", Scaffold }, "new-idea refused: 'o/r' is an existing repo; scaffold options are only for a new one")]
    [InlineData(new[] { "new-idea", "x", "-Target", "o/new" }, "new-idea refused: 'o/new' is a new repo, so it needs scaffold options")]
    [InlineData(new[] { "new-idea", "x", "-Target", "o/new", "-Scaffold", "{nope" }, "new-idea refused: the scaffold options are not valid JSON")]
    [InlineData(new[] { "new-idea", "x", "-Target", "o/new", "-Scaffold", """{"name":"Todo","resource":"Todo","db":"sqlite","ui":"none"}""" }, "new-idea refused: scaffold name 'Todo' must be lower-case letters and digits, starting with a letter")]
    [InlineData(new[] { "new-idea", "x", "-Target", "o/new", "-Base", "J-001" }, "base refused: 'o/new' is a new repo")]
    [InlineData(new[] { "new-idea", "x", "-Target", "o/r", "-Base", "J-001" }, "base refused: no job J-001")]
    public void NewIdea_refuses_and_writes_nothing(string[] args, string refusal)
    {
        _gh.Lookup("o/new").Returns(RepoState.Missing);
        var s = FakeSession.With();

        Assert.Equal(refusal, Assert.Throws<FactoryException>(() => NewIdea.Run(Args(args), s)).Message);
        Assert.Equal(0, s.Saves);
    }

    private static readonly string ProductFile = Path.Combine(Path.GetTempPath(), "S-001", "product.md");
    private const string ProductMd = "# Product: Todo\n## Home\n- Home: redirect: /todos\n## Features\n### Add a todo\nType it in.\n### Share a list\nWith a friend.\n";

    [Fact]
    public void NewIdea_with_a_product_is_a_repo_idea_and_keeps_its_product()
    {
        _gh.Lookup("o/new").Returns(RepoState.Missing);
        Files.Put(ProductFile, ProductMd);
        var s = FakeSession.With();

        var r = NewIdea.Run(Args("new-idea", "todo app", "-Target", "o/new", "-Scaffold", Scaffold, "-Product", ProductFile), s);

        Assert.Equal(["I-001 J-001"], r.Out);
        Assert.Equal(("repo", "waiting"), ((string?)Idea(s)["kind"], (string?)Idea(s)["status"]));
        Assert.Equal(ProductMd, Files.Text(Path.Combine(Home, "ideas", "I-001", "product.md")));
    }

    [Theory]
    [InlineData("o/r", true, "new-idea refused: 'o/r' is an existing repo; a product is only for a new one")]
    [InlineData("o/new", false, "new-idea refused: no product file")]
    public void NewIdea_refuses_a_product_out_of_place(string target, bool present, string refusal)
    {
        _gh.Lookup("o/new").Returns(RepoState.Missing);
        if (present) Files.Put(ProductFile, ProductMd);
        string[] scaffold = target == "o/new" ? ["-Scaffold", Scaffold] : [];
        var s = FakeSession.With();

        Assert.Equal(refusal, Assert.Throws<FactoryException>(() => NewIdea.Run(Args(["new-idea", "x", "-Target", target, .. scaffold, "-Product", ProductFile]), s)).Message);
        Assert.Equal(0, s.Saves);
    }

    [Fact]
    public void NewIdea_refuses_a_product_the_rules_refuse()
    {
        _gh.Lookup("o/new").Returns(RepoState.Missing);
        Files.Put(ProductFile, "# Product: Todo\n## Home\n- Home: redirect: /todos\n## Features\n");

        Assert.Equal("new-idea refused: list 1 to 10 features, each a ### heading under ## Features (it has 0)",
            Assert.Throws<FactoryException>(() => NewIdea.Run(Args("new-idea", "x", "-Target", "o/new", "-Scaffold", Scaffold, "-Product", ProductFile), FakeSession.With())).Message);
    }

    private static FakeSession WithRepoIdea(string status = "drafting", string target = "o/r", JsonArray? features = null)
    {
        var s = FakeSession.With();
        s.Board["ideas"]!.AsArray().Add(new JsonObject
        {
            ["id"] = "I-001", ["text"] = "todo app", ["target"] = target, ["status"] = status, ["kind"] = "repo",
            ["features"] = features ?? new JsonArray(), ["jobs"] = new JsonArray(),
        });
        return s;
    }

    [Fact]
    public void NewIdea_from_a_repo_idea_starts_that_feature()
    {
        var s = WithRepoIdea();

        var r = NewIdea.Run(Args("new-idea", "Add a todo", "-Target", "o/r", "-From", "I-001", "-Feature", "Add a todo"), s);

        Assert.Equal(["I-002"], r.Out);
        var feature = s.Board["ideas"]![1]!.AsObject();
        Assert.Equal(("I-001", "Add a todo", "drafting"), ((string?)feature["from"], (string?)feature["feature"], (string?)feature["status"]));
        Assert.Equal("""[{"heading":"Add a todo","idea":"I-002"}]""", Idea(s)["features"]!.ToJsonString());
    }

    public static TheoryData<string[], string?, string> FromRefusals => new()
    {
        { ["-From", "I-001"], null, "new-idea refused: -From and -Feature go together" },
        { ["-Feature", "Add a todo"], null, "new-idea refused: -From and -Feature go together" },
        { ["-From", "I-009", "-Feature", "Add a todo"], null, "new-idea refused: no idea I-009" },
        { ["-From", "I-001", "-Feature", "Add a todo"], "waiting", "new-idea refused: I-001 is waiting, not a drafting repo idea" },
        { ["-From", "I-001", "-Feature", "Add a todo"], "other", "new-idea refused: I-001 is a repo idea on o/other, not o/r" },
        { ["-From", "I-001", "-Feature", " add A TODO"], "started", "new-idea refused: feature 'Add a todo' of I-001 already has I-005" },
    };

    [Theory]
    [MemberData(nameof(FromRefusals))]
    public void NewIdea_refuses_a_feature_it_cannot_start(string[] from, string? setup, string refusal)
    {
        var s = setup switch
        {
            "waiting" => WithRepoIdea("waiting"),
            "other" => WithRepoIdea(target: "o/other"),
            "started" => WithRepoIdea(features: [new JsonObject { ["heading"] = "Add a todo", ["idea"] = "I-005" }]),
            _ => WithRepoIdea(),
        };

        Assert.Equal(refusal, Assert.Throws<FactoryException>(() => NewIdea.Run(Args(["new-idea", "x", "-Target", "o/r", .. from]), s)).Message);
        Assert.Equal(0, s.Saves);
    }

    [Fact]
    public void NewIdea_refuses_a_feature_of_an_idea_that_is_not_a_repo_idea()
    {
        var s = WithIdea("drafting");

        Assert.Equal("new-idea refused: I-001 is not a repo idea",
            Assert.Throws<FactoryException>(() => NewIdea.Run(Args("new-idea", "x", "-Target", "o/r", "-From", "I-001", "-Feature", "A"), s)).Message);
    }

    [Fact]
    public void NewIdea_refuses_when_GitHub_does()
    {
        _gh.Lookup("o/r").Throws(new FactoryException("GitHub 401: Bad credentials"));

        Assert.Equal("new-idea refused: GitHub 401: Bad credentials",
            Assert.Throws<FactoryException>(() => NewIdea.Run(Args("new-idea", "x", "-Target", "o/r"), FakeSession.With())).Message);
    }

    [Fact]
    public void AddSpec_adds_a_backlog_job_from_a_file_relative_to_the_working_directory()
    {
        var s = WithIdea("drafting");
        Files.Put(Path.Combine(Cwd, "s.md"), "# Spec: Book search\n\n## Target\n- Repo: o/r\n");

        var r = AddSpec.Run(Args("add-spec", "I-001", "s.md"), s);

        Assert.Equal(["J-001"], r.Out);
        Assert.Equal($"# Spec: J-001 — Book search{Nl}{Nl}## Target{Nl}- Repo: o/r{Nl}", Files.Text(JobFile("J-001", "spec.md")));
        Assert.Equal(("feature", "Book search", "o/r"), ((string?)s.Job("J-001")["kind"], (string?)s.Job("J-001")["title"], (string?)s.Job("J-001")["target"]));
        Assert.Equal(["stage"], s.Kinds);
    }

    [Fact]
    public void AddSpec_stamps_no_provider_whatever_env_says()
    {
        // The run mode picks the provider for every job when it runs (contract, "Run mode and providers").
        DotEnv.Get("FACTORY_MODE").Returns("cloud");
        var s = WithIdea("drafting");
        Files.Put(Path.Combine(Cwd, "s.md"), "# Spec: Book search\n");

        AddSpec.Run(Args("add-spec", "I-001", "s.md"), s);

        Assert.DoesNotContain(s.Job("J-001").Select(kv => kv.Key), k => k.Contains("provider", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(new[] { "add-spec", "I-009", "s.md" }, "no idea I-009")]
    [InlineData(new[] { "add-spec", "I-001", "missing.md" }, "spec file not found: missing.md")]
    [InlineData(new[] { "add-spec", "I-001" }, "spec file not found: ")]
    [InlineData(new[] { "add-spec", "I-001", "bad.md" }, "no '# Spec:' heading in bad.md")]
    public void AddSpec_refuses(string[] args, string refusal)
    {
        var s = WithIdea("drafting");
        Files.Put(Path.Combine(Cwd, "s.md"), "# Spec: x\n");
        Files.Put(Path.Combine(Cwd, "bad.md"), "no heading\n");

        Assert.Equal(refusal, Assert.Throws<FactoryException>(() => AddSpec.Run(Args(args), s)).Message);
        Assert.Equal(0, s.Saves);
    }

    [Fact]
    public void IdeaDone_closes_and_Failed_keeps_the_reason()
    {
        var s = WithIdea("drafting");
        new IdeaDoneCommand(Clock).Run(Args("idea-done", "I-001"), s);
        Assert.Equal(("done", null), ((string?)Idea(s)["status"], Idea(s)["reason"]));

        new IdeaDoneCommand(Clock).Run(Args("idea-done", "I-001", "-Failed", "-Reason", "too vague"), s);

        Assert.Equal(("failed", "too vague"), ((string?)Idea(s)["status"], (string?)Idea(s)["reason"]));
        Assert.Equal(["done", "failed"], s.Events.Select(e => (string?)e["detail"]));
    }

    [Fact]
    public void IdeaDone_with_a_spec_in_backlog_keeps_the_idea_open_until_its_last_spec_is_decided()
    {
        // Spec 22: Not yet keeps the idea open on its card in Ideas.
        var s = WithIdea("drafting", Jobs.New("J-001", "backlog"), Jobs.New("J-002", "backlog"), Jobs.New("J-003", "approved"));
        Files.Put(JobFile("J-001", "spec.md"), ApprovableSpec);

        var r = new IdeaDoneCommand(Clock).Run(Args("idea-done", "I-001"), s);

        Assert.Equal(["I-001 stays open: J-001, J-002 wait in backlog"], r.Out);
        Assert.Equal(("drafting", true), ((string?)Idea(s)["status"], (bool?)Idea(s)["closeWhenDecided"]));
        Assert.Empty(s.Events);

        new ApproveCommand(Paths, Files, Clock).Run(Args("approve", "J-001"), s);
        Assert.Equal("drafting", (string?)Idea(s)["status"]);

        new DiscardCommand(Substitute.For<IJobWork>(), Clock).Run(Args("discard", "J-002"), s);
        Assert.Equal(("done", null), ((string?)Idea(s)["status"], Idea(s)["closeWhenDecided"]));
        Assert.Equal("""{"ts":"2026-11-10T12:00:00.000Z","idea":"I-001","kind":"idea","detail":"done"}""", s.Events.Last().ToJsonString());
    }

    [Fact]
    public void Approving_a_spec_of_an_idea_still_being_specced_leaves_it_open()
    {
        var s = WithIdea("drafting", Jobs.New("J-001", "backlog"));
        Files.Put(JobFile("J-001", "spec.md"), ApprovableSpec);

        new ApproveCommand(Paths, Files, Clock).Run(Args("approve", "J-001"), s);

        Assert.Equal("drafting", (string?)Idea(s)["status"]);
    }

    [Fact]
    public void IdeaDone_Failed_closes_whatever_waits_in_backlog()
    {
        var s = WithIdea("drafting", Jobs.New("J-001", "backlog"));

        new IdeaDoneCommand(Clock).Run(Args("idea-done", "I-001", "-Failed", "-Reason", "r"), s);

        Assert.Equal(("failed", null), ((string?)Idea(s)["status"], Idea(s)["closeWhenDecided"]));
    }

    [Fact]
    public void IdeaDone_Failed_without_a_reason_keeps_an_empty_one()
    {
        var s = WithIdea("drafting");

        new IdeaDoneCommand(Clock).Run(Args("idea-done", "I-001", "-Failed"), s);

        Assert.Equal("", (string?)Idea(s)["reason"]);
    }

    [Theory]
    [InlineData("waiting")]
    [InlineData("drafting")]
    [InlineData("Failed")]
    public void IdeaAbandon_abandons_an_open_idea(string status)
    {
        var s = WithIdea(status);

        new IdeaAbandonCommand(Clock).Run(Args("idea-abandon", "I-001"), s);

        Assert.Equal("abandoned", (string?)Idea(s)["status"]);
        Assert.Equal("""{"ts":"2026-11-10T12:00:00.000Z","idea":"I-001","kind":"idea","detail":"abandoned"}""", s.Events.Single().ToJsonString());
    }

    [Theory]
    [InlineData("done")]
    [InlineData("abandoned")]
    public void IdeaAbandon_refuses_a_closed_idea(string status)
    {
        var s = WithIdea(status);

        Assert.Equal($"idea-abandon refused: I-001 is {status}", Assert.Throws<FactoryException>(() => new IdeaAbandonCommand(Clock).Run(Args("idea-abandon", "I-001"), s)).Message);
        Assert.Equal(0, s.Saves);
    }
}
