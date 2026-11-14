using Factory.Cli;

namespace Factory.UnitTests;

public class EnvSettingsTests
{
    private const string Env = "repo/.env";
    private readonly MemoryFiles _files = new();

    private string[] Lines() => _files.Text(Env)!.ReplaceLineEndings("\n").TrimEnd('\n').Split('\n');

    [Fact]
    public void A_new_env_starts_from_the_example_and_an_existing_one_is_kept()
    {
        _files.Put("repo/.env.example", "GITHUB_OWNER=\n# HCP_ORG=\n");
        new EnvSettings(_files).EnsureFrom(Env, "repo/.env.example");
        Assert.Equal(["GITHUB_OWNER=", "# HCP_ORG="], Lines());

        _files.Put(Env, "MINE=1\n");
        new EnvSettings(_files).EnsureFrom(Env, "repo/.env.example");
        Assert.Equal(["MINE=1"], Lines());
    }

    [Fact]
    public void Set_replaces_the_live_line_else_a_commented_one_else_appends_and_keeps_every_other_line()
    {
        _files.Put(Env, "# a comment\nGITHUB_OWNER=old\n# FACTORY_MODE=local\nOTHER=x\n");
        var e = new EnvSettings(_files);

        e.Set(Env, "GITHUB_OWNER", "kamabery");
        e.Set(Env, "FACTORY_MODE", "cloud");
        e.Set(Env, "FACTORY_FOUNDRY_RESOURCE", "fdy");

        Assert.Equal(["# a comment", "GITHUB_OWNER=kamabery", "FACTORY_MODE=cloud", "OTHER=x", "FACTORY_FOUNDRY_RESOURCE=fdy"], Lines());
    }

    [Fact]
    public void Comment_out_returns_the_value_and_leaves_a_note_in_its_place()
    {
        _files.Put(Env, "HCP_TOKEN=\"abc\"\nKEEP=1\n");

        Assert.Equal("abc", new EnvSettings(_files).CommentOut(Env, "HCP_TOKEN", "moved to the secret store"));
        Assert.Equal(["# HCP_TOKEN moved to the secret store", "KEEP=1"], Lines());
        Assert.Null(new EnvSettings(_files).CommentOut(Env, "HCP_TOKEN", "moved"));
    }

    [Theory]
    [InlineData(new string[0], null, null, false, false)]
    [InlineData(new[] { "--org", "acme", "--foundry", "fdy", "--move-env-secrets", "--no-open" }, "acme", "fdy", true, true)]
    public void Init_parses_its_flags(string[] args, string? org, string? foundry, bool move, bool noOpen) =>
        Assert.Equal(new InitOptions(org, foundry, move, noOpen), InitOptions.Parse(args));
}
