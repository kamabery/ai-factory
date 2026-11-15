using System.Text.Json.Nodes;
using Factory.Core;

namespace Factory.UnitTests;

public class GitHubErrorsTests
{
    [Fact]
    public void A_message_body_reads_GitHub_status_message()
    {
        var r = new GitHubResponse(404, JsonNode.Parse("""{"message":"Not Found"}"""), null);

        Assert.Equal("GitHub 404: Not Found", GitHubErrors.Text(r));
    }

    [Fact]
    public void A_text_body_is_the_message()
    {
        Assert.Equal("GitHub 502: bad gateway", GitHubErrors.Text(new GitHubResponse(502, JsonValue.Create("bad gateway"), null)));
    }

    [Fact]
    public void No_message_leaves_the_status_trimmed()
    {
        Assert.Equal("GitHub 500:", GitHubErrors.Text(new GitHubResponse(500, JsonNode.Parse("[1]"), null)));
        Assert.Equal("GitHub 500:", GitHubErrors.Text(new GitHubResponse(500, null, null)));
    }

    [Fact]
    public void A_denied_permission_says_what_the_App_needs()
    {
        var r = new GitHubResponse(403, JsonNode.Parse("""{"message":"Resource not accessible by personal access token"}"""), "environments=write");

        Assert.Equal(
            "GitHub 403: Resource not accessible by personal access token; the GitHub App needs environments=write (add it in the App's settings, then accept it on the installation)",
            GitHubErrors.Text(r));
    }

    [Fact]
    public void AppNeeds_is_the_sentence_deploy_matches()
    {
        var text = GitHubErrors.AppNeeds("variables=write");

        Assert.Equal("the GitHub App needs variables=write (add it in the App's settings, then accept it on the installation)", text);
        Assert.Equal("variables=write", GitHubErrors.AppNeedsPattern.Match(text).Groups[1].Value);
    }
}
