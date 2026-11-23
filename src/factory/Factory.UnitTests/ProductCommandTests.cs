using System.Text;
using System.Text.Json.Nodes;
using Factory.Cli;
using Factory.Core;
using Factory.Store.Abstractions;
using NSubstitute;

namespace Factory.UnitTests;

public class ProductCommandTests : CommandTestBase
{
    private const string Path_ = "/repos/o/r/contents/docs/PRODUCT.md?ref=main";
    private const string ProductMd = "# Product: Todo\n## Home\n- Home: redirect: /todos\n## Features\n### Add a todo\nType it in.\n### Share a list\nWith a friend.\n";
    private readonly IGitHub _gh = Substitute.For<IGitHub>();

    private ProductCommand Product => new(_gh);

    private static FakeSession WithRepoIdea(string kind = "repo", JsonArray? features = null)
    {
        var s = FakeSession.With();
        var idea = new JsonObject { ["id"] = "I-001", ["text"] = "todo", ["target"] = "o/r", ["status"] = "drafting", ["jobs"] = new JsonArray() };
        if (kind.Length > 0) { idea["kind"] = kind; idea["features"] = features ?? new JsonArray(); }
        s.Board["ideas"]!.AsArray().Add(idea);
        return s;
    }

    private void OnMain(string text) =>
        _gh.Send(HttpMethod.Get, Path_).Returns(new GitHubResponse(200, new JsonObject
        {
            ["encoding"] = "base64", ["content"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(text)).Insert(8, "\n"),
        }, null));

    [Fact]
    public void Product_lists_the_features_on_main_and_the_next_one_to_start()
    {
        OnMain(ProductMd);
        var s = WithRepoIdea(features: [new JsonObject { ["heading"] = "add a TODO ", ["idea"] = "I-002" }]);

        var r = Product.Run(Args("product", "I-001"), s);

        Assert.Equal(
            """{"features":[{"k":1,"heading":"Add a todo","text":"Type it in.","idea":"I-002"},{"k":2,"heading":"Share a list","text":"With a friend.","idea":null}],"next":2,"home":"- Home: redirect: /todos"}""",
            r.Out.Single());
        Assert.Equal(0, s.Saves);
    }

    [Fact]
    public void Product_has_no_next_when_every_feature_has_an_idea()
    {
        OnMain(ProductMd);
        var s = WithRepoIdea(features: [new JsonObject { ["heading"] = "Add a todo", ["idea"] = "I-002" }, new JsonObject { ["heading"] = "Share a list", ["idea"] = "I-003" }]);

        Assert.Null(JsonNode.Parse(Product.Run(Args("product", "I-001"), s).Out.Single())!["next"]);
    }

    [Fact]
    public void Product_refuses_an_idea_that_is_not_a_repo_idea() =>
        Assert.Equal("product refused: I-001 is not a repo idea", Assert.Throws<FactoryException>(() => Product.Run(Args("product", "I-001"), WithRepoIdea(""))).Message);

    [Fact]
    public void Product_refuses_when_main_has_no_PRODUCT_md()
    {
        _gh.Send(HttpMethod.Get, Path_).Returns(new GitHubResponse(404, new JsonObject { ["message"] = "Not Found" }, null));

        Assert.Equal("product refused: docs/PRODUCT.md on main: GitHub 404: Not Found",
            Assert.Throws<FactoryException>(() => Product.Run(Args("product", "I-001"), WithRepoIdea())).Message);
    }

    [Fact]
    public void Product_refuses_a_PRODUCT_md_with_no_feature()
    {
        OnMain("# Product: Todo\n## Home\n- Home: keep: fine\n## Features\n");

        Assert.Equal("product refused: docs/PRODUCT.md on main has no feature to start",
            Assert.Throws<FactoryException>(() => Product.Run(Args("product", "I-001"), WithRepoIdea())).Message);
    }
}
