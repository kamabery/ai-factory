using System.Text.Json.Nodes;
using Factory.Core;

namespace Factory.UnitTests;

public class PreviewTests
{
    [Theory]
    [InlineData("J-012", "j012")]
    [InlineData("canary", "j")]
    public void Env_is_j_and_the_ids_digits(string id, string env) => Assert.Equal(env, Preview.Env(id));

    [Theory]
    [InlineData("o/todo", "todo")]
    [InlineData("noslash", "")]
    public void RepoName_is_the_part_after_the_owner(string target, string name) => Assert.Equal(name, Preview.RepoName(target));

    [Fact]
    public void Record_creates_the_deploy_record_once_in_the_contracts_key_order()
    {
        var job = new Job(Jobs.New("J-007", "deploy", target: "o/todo"));

        var d = Preview.Record(job);

        Assert.Equal("""{"env":"j007","workspace":"todo-j007","workspaceId":null,"status":null,"attempts":0,"sha":null,"run":null,"urls":null,"appliedAt":null,"destroyAt":null}""", d.ToJsonString());
        Assert.Same(d, job.Node["deploy"]);
        Assert.Same(d, Preview.Record(job));
    }

    [Fact]
    public void Urls_reach_health_through_the_react_front_end_else_through_the_api()
    {
        Assert.Equal("""{"frontend":"https://web","api":"https://api","health":"https://web/api/health"}""",
            Preview.Urls(new Dictionary<string, string?> { ["react_url"] = "https://web", ["api_url"] = "https://api" }).ToJsonString());
        Assert.Equal("""{"frontend":"https://blazor","api":"https://api/","health":"https://api/health"}""",
            Preview.Urls(new Dictionary<string, string?> { ["blazor_web_url"] = "https://blazor", ["api_url"] = "https://api/" }).ToJsonString());
        Assert.Equal("""{"frontend":null,"api":null,"health":null}""", Preview.Urls(new Dictionary<string, string?>()).ToJsonString());
    }

    [Fact]
    public void A_react_url_keeps_its_own_spelling_as_the_front_end()
    {
        var urls = Preview.Urls(new Dictionary<string, string?> { ["react_url"] = "https://web/" });
        Assert.Equal(("https://web/", "https://web/api/health"), ((string?)urls["frontend"], (string?)urls["health"]));
    }

    [Theory]
    [InlineData("on:\n  workflow_dispatch:\n    inputs:\n      environment:\n      since:\n", true)]
    [InlineData("on:\n  workflow_dispatch:\n    inputs:\n      environment:\n", false)]
    [InlineData("since: at the top level is not an input\n", false)]
    [InlineData(null, false)]
    public void DeclaresSince_reads_the_branchs_workflow(string? yml, bool declared) => Assert.Equal(declared, Preview.DeclaresSince(yml));

    [Fact]
    public void OidcSubject_uses_the_immutable_prefix_only_when_the_repo_has_one()
    {
        Assert.Equal("repo:o@1/n@2:environment:preview",
            Preview.OidcSubject("o/n", JsonNode.Parse("""{"use_immutable_subject":true,"sub_claim_prefix":"repo:o@1/n@2"}""")));
        Assert.Equal("repo:o/n:environment:preview", Preview.OidcSubject("o/n", JsonNode.Parse("""{"use_default":true}""")));
        Assert.Equal("repo:o/n:environment:preview", Preview.OidcSubject("o/n", JsonNode.Parse("""{"use_immutable_subject":true}""")));
        Assert.Equal("repo:o/n:environment:preview", Preview.OidcSubject("o/n", null));
    }

    [Fact]
    public void CredentialName_keeps_letters_digits_and_dashes() => Assert.Equal("preview-my-org-todo-app", Preview.CredentialName("my-org/todo.app"));
}
