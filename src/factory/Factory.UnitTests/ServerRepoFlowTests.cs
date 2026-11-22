using System.Text.Json.Nodes;
using Factory.Server;
using Factory.Server.Abstractions;
using NSubstitute;

namespace Factory.UnitTests;

/// <summary>
/// Spec 22: a New repo session grills to a product, scaffolds and stops; its repo card then starts each feature in a
/// session of its own (contract, "Idea sessions (dashboard)", "New repo and its features").
/// </summary>
public class ServerRepoFlowTests : ServerTestBase
{
    private const string ProductJson =
        """{"features":[{"k":1,"heading":"Add a todo","text":"Type it in.","idea":null},{"k":2,"heading":"Share a list","text":"With a friend.","idea":null}],"next":1,"home":"- Home: redirect: /todos"}""";

    private readonly ProductReads _products;

    public ServerRepoFlowTests()
    {
        _products = new ProductReads(Factory, Clock);
        Settings.Env("GITHUB_OWNER").Returns("me");
        Factory.Run("product", "I-004").Returns(CallResult.Done(ProductJson));
        Factory.Run("check-target", "me/fresh").Returns(CallResult.Done("existing"));
    }

    private IdeaSessions Repo => new(Factory, Launcher, Paths, Files, Settings, Board, Sessions, Jobs, Sync, State, Opts, Clock, _products);

    private string SessionFile(string id, string name) => Path.Combine(Home, "sessions", id, name);

    private JsonObject Session(string id = "S-001") => JsonNode.Parse(Files.Text(SessionFile(id, "session.json"))!)!.AsObject();

    private void PutSession(string json, string id = "S-001")
    {
        Files.Put(SessionFile(id, "session.json"), json);
        Files.Put(SessionFile(id, "chat.json"), """{"messages":[{"role":"user","content":[{"type":"text","text":"hi"}]},{"role":"assistant","content":[]}]}""");
    }

    private void PutBoard(string bootStage, string ideaStatus, string features = "[]") =>
        Files.Put(Path.Combine(Home, "board.json"),
            $$"""{"jobs":[{"id":"J-010","stage":"{{bootStage}}","title":"Bootstrap","kind":"bootstrap"}],"ideas":[{"id":"I-004","status":"{{ideaStatus}}","target":"me/fresh","kind":"repo","features":{{features}}}]}""");

    private int Turns(string id = "S-001") => Launcher.ReceivedCalls().Count(c => c.GetArguments()[0] is IReadOnlyList<string> a && a.SequenceEqual(new[] { "idea-turn", id }));

    private int Runners() => Launcher.ReceivedCalls().Count(c => c.GetArguments()[0] is IReadOnlyList<string> a && a.SequenceEqual(new[] { "runner" }));

    // --- the New repo session ----------------------------------------------------------------------

    [Fact]
    public void New_repo_opens_a_repo_session_that_grills_to_a_product()
    {
        var r = Repo.New("""{"new":true,"text":"a todo app"}""");

        Assert.Equal(200, r.Status);
        Assert.Equal("repo", (string?)Session()["kind"]);
        Assert.Contains("submit_product", Files.Text(SessionFile("S-001", "chat.json")));
    }

    [Fact]
    public void A_picked_repo_that_is_new_opens_a_repo_session_too()
    {
        Factory.Run("check-target", "me/empty").Returns(CallResult.Done("new"));

        Repo.New("""{"target":"me/empty","text":"a todo app"}""");

        Assert.Equal("repo", (string?)Session()["kind"]);
    }

    [Fact]
    public void Confirming_a_product_records_a_repo_idea_approves_its_bootstrap_and_writes_no_brief()
    {
        PutSession("""{"id":"S-001","status":"idle","text":"a todo app","target":"me/fresh","newTarget":true,"kind":"repo","scaffold":{"ui":"react"},"pending":{"id":"tu_3","name":"submit_product","input":{"markdown":"# Product: Todo\n## Features\n### A\n"}}}""");
        var product = SessionFile("S-001", "product.md");
        Factory.Run("new-idea", "Todo", "-Target", "me/fresh", "-Scaffold", """{"ui":"react"}""", "-Product", product).Returns(CallResult.Done("I-004 J-010"));

        var r = Repo.Handle("POST", "S-001", "brief", null, "");

        Assert.Equal(200, r.Status);
        Assert.Equal("# Product: Todo\n## Features\n### A\n" + Environment.NewLine, Files.Text(product));
        Factory.Received(1).Run("approve", "J-010");
        Assert.Equal((1, 0), (Runners(), Turns()));
        Assert.Null(Files.Text(Path.Combine(Home, "ideas", "I-004", "brief.md")));
        Assert.Equal(("I-004", "J-010", null), ((string?)Session()["idea"], (string?)Session()["bootstrap"], (string?)Session()["pending"]));
    }

    [Fact]
    public void A_repo_session_gets_no_spec_turn_when_its_bootstrap_merges()
    {
        PutSession("""{"id":"S-001","status":"idle","target":"me/fresh","newTarget":true,"kind":"repo","idea":"I-004","bootstrap":"J-010"}""");
        PutBoard("done", "drafting");

        Repo.Sweep();

        Assert.Equal(0, Turns());
        Assert.Null(Session()["specsStarted"]);
    }

    // --- the repo card ----------------------------------------------------------------------------

    [Fact]
    public void The_product_read_is_cached_for_60_seconds()
    {
        Assert.Equal(ProductJson, _products.Read("I-004").Body);
        Assert.Equal(ProductJson, _products.Read("I-004").Body);
        Clock.Advance(TimeSpan.FromSeconds(61));
        _products.Read("I-004");

        Factory.Received(2).Run("product", "I-004");
    }

    [Fact]
    public void A_refused_product_read_answers_the_refusal_and_is_not_cached()
    {
        Factory.Run("product", "I-004").Returns(CallResult.Refuse("product refused: docs/PRODUCT.md on main has no feature to start"));

        Assert.Equal("""{"ok":false,"message":"product refused: docs/PRODUCT.md on main has no feature to start"}""", _products.Read("I-004").Body);
        _products.Read("I-004");

        Factory.Received(2).Run("product", "I-004");
    }

    [Fact]
    public void Implement_opens_a_feature_session_on_the_repo_seeded_with_the_feature_and_its_home()
    {
        PutBoard("done", "drafting");

        var r = Repo.Implement("I-004");

        Assert.Equal("""{"ok":true,"message":"S-001"}""", r.Body);
        var s = Session();
        Assert.Equal(("me/fresh", "Add a todo", "I-004", "Add a todo", "- Home: redirect: /todos", false),
            ((string?)s["target"], (string?)s["text"], (string?)s["from"], (string?)s["feature"], (string?)s["home"], (bool?)s["newTarget"]));
        Factory.Received(1).Run("clone-target", "me/fresh", Path.Combine(Work, "sessions", "S-001"));
        var chat = Files.Text(SessionFile("S-001", "chat.json"))!;
        Assert.Contains("Type it in.", chat);
        Assert.Contains("docs/PRODUCT.md", chat);
        Assert.Contains("- Home: redirect: /todos", chat);
        Assert.Equal(1, Turns());
    }

    [Fact]
    public void A_later_feature_needs_no_home()
    {
        PutBoard("done", "drafting", """[{"heading":"Add a todo","idea":"I-005"}]""");
        Factory.Run("product", "I-004").Returns(CallResult.Done(ProductJson.Replace("\"idea\":null},{\"k\":2", "\"idea\":\"I-005\"},{\"k\":2").Replace("\"next\":1", "\"next\":2")));

        Repo.Implement("I-004");

        Assert.Equal(("Share a list", null), ((string?)Session()["feature"], (string?)Session()["home"]));
    }

    [Fact]
    public void Implement_is_refused_while_a_session_for_that_feature_is_open()
    {
        PutBoard("done", "drafting");
        PutSession("""{"id":"S-001","status":"idle","target":"me/fresh","from":"I-004","feature":"Add a todo"}""");

        Assert.Equal("""{"ok":false,"message":"S-001 is already working on 'Add a todo'"}""", Repo.Implement("I-004").Body);
    }

    [Fact]
    public void Implement_is_refused_with_no_feature_left()
    {
        PutBoard("done", "drafting");
        Factory.Run("product", "I-004").Returns(CallResult.Done("""{"features":[],"next":null,"home":null}"""));

        Assert.Equal("""{"ok":false,"message":"I-004 has no feature left to start"}""", Repo.Implement("I-004").Body);
    }

    [Fact]
    public void Confirming_a_features_brief_starts_that_feature_and_the_last_one_closes_the_repo_idea()
    {
        PutBoard("done", "drafting");
        PutSession("""{"id":"S-002","status":"idle","target":"me/fresh","kind":"repo","idea":"I-004","bootstrap":"J-010"}""", "S-002");
        PutSession("""{"id":"S-003","status":"idle","target":"me/fresh","from":"I-004","feature":"Share a list","pending":{"id":"tu_1","name":"submit_brief","input":{"markdown":"# Brief: Share a list"}}}""", "S-003");
        Factory.Run("new-idea", "Share a list", "-Target", "me/fresh", "-From", "I-004", "-Feature", "Share a list").Returns(CallResult.Done("I-006"));
        _products.Read("I-004");
        Factory.Run("product", "I-004").Returns(CallResult.Done("""{"features":[],"next":null,"home":null}"""));

        var r = Repo.Handle("POST", "S-003", "brief", null, "");

        Assert.Equal(200, r.Status);
        Assert.Equal("I-006", (string?)Session("S-003")["idea"]);
        Assert.Equal(1, Turns("S-003"));
        Factory.Received(1).Run("idea-done", "I-004");
        Assert.Equal("closed", (string?)Session("S-002")["status"]);
    }

    [Fact]
    public void Dismiss_closes_the_repo_idea_and_its_session()
    {
        PutBoard("done", "drafting");
        PutSession("""{"id":"S-002","status":"idle","target":"me/fresh","kind":"repo","idea":"I-004","bootstrap":"J-010"}""", "S-002");

        var r = Repo.Dismiss("I-004");

        Assert.Equal(200, r.Status);
        Factory.Received(1).Run("idea-done", "I-004");
        Assert.Equal("closed", (string?)Session("S-002")["status"]);
    }

    [Fact]
    public void The_session_summary_carries_kind_from_and_feature()
    {
        PutBoard("done", "drafting");
        PutSession("""{"id":"S-003","status":"idle","target":"me/fresh","from":"I-004","feature":"Share a list","kind":null}""", "S-003");

        var list = JsonNode.Parse(Reads.Sessions().Body)!.AsArray();

        Assert.Equal(("I-004", "Share a list"), ((string?)list[0]!["from"], (string?)list[0]!["feature"]));
        Assert.True(list[0]!.AsObject().ContainsKey("kind"));
    }
}
