using Factory.Cli;
using Factory.Core;
using Factory.Core.Abstractions;
using Factory.Store.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Factory.UnitTests;

/// <summary>init's template workspace step (contract, "Secrets, init and doctor", step 4).</summary>
public class TemplateWorkspaceTests
{
    private static readonly string Profile = Path.Combine("u", ".ai-factory");
    private static readonly string Default = Path.Combine(Profile, "template-projects");
    private readonly IFactoryPaths _paths = Substitute.For<IFactoryPaths>();
    private readonly IFileStore _files = Substitute.For<IFileStore>();
    private readonly IGitClient _git = Substitute.For<IGitClient>();
    private readonly IDotEnv _env = Substitute.For<IDotEnv>();
    private readonly IGitHub _github = Substitute.For<IGitHub>();
    private readonly IGitHubCredential _credential = Substitute.For<IGitHubCredential>();
    private readonly StringWriter _out = new() { NewLine = "\n" };

    public TemplateWorkspaceTests()
    {
        _paths.Profile.Returns(Profile);
        _paths.TemplatesRoot.Returns(Default);
        _env.Get(Arg.Any<string>()).Returns((string?)null);
        _git.Run(Arg.Any<string>(), Arg.Any<string[]>()).Returns(new GitResult(0, ""));
        _github.CloneUrl(Arg.Any<string>()).Returns(c => $"https://github.com/{c.Arg<string>()}.git");
        _credential.Token(Arg.Any<string?>()).Returns((string?)null);
    }

    private TemplateWorkspace Sut => new(_paths, _files, _git, _env, _github, _credential);

    [Fact]
    public void A_tool_install_clones_the_default_repo_into_the_profile_anonymously_with_no_App()
    {
        Sut.Ensure(_out);

        _files.Received(1).EnsureDirectory(Profile);
        _git.Received(1).Run(Profile, "-c", "credential.helper=", "clone", "https://github.com/kamabery/template-projects.git", Default);
        Assert.Equal($"ok    templates: cloned kamabery/template-projects to {Default}\n", _out.ToString());
    }

    [Fact]
    public void It_clones_as_the_Apps_installation_on_the_repos_owner_so_a_private_repo_clones()
    {
        _credential.Token("kamabery").Returns("ghs_1");
        _github.GitAuth("kamabery").Returns(["-c", "auth"]);

        Sut.Ensure(_out);

        _git.Received(1).Run(Profile, "-c", "auth", "clone", "https://github.com/kamabery/template-projects.git", Default);
    }

    [Fact]
    public void An_owner_the_App_is_not_installed_on_is_cloned_anonymously()
    {
        _env.Get("FACTORY_TEMPLATES_REPO").Returns("someone/templates");
        _credential.Token("someone").Throws(new FactoryException("the GitHub App is not installed on someone"));

        Sut.Ensure(_out);

        _git.Received(1).Run(Profile, "-c", "credential.helper=", "clone", "https://github.com/someone/templates.git", Default);
    }

    [Fact]
    public void FACTORY_TEMPLATES_REPO_names_the_repo()
    {
        _env.Get("FACTORY_TEMPLATES_REPO").Returns("me/my-templates");

        Sut.Ensure(_out);

        _git.Received(1).Run(Profile, "-c", "credential.helper=", "clone", "https://github.com/me/my-templates.git", Default);
    }

    [Fact]
    public void One_that_exists_is_left_as_it_is()
    {
        _files.DirectoryExists(Default).Returns(true);

        Sut.Ensure(_out);

        _git.DidNotReceiveWithAnyArgs().Run(default!, default!);
        Assert.Equal($"ok    templates: {Default}\n", _out.ToString());
    }

    [Fact]
    public void A_checkout_install_or_a_set_FACTORY_TEMPLATES_skips_the_step()
    {
        _paths.Profile.Returns((string?)null);
        Sut.Ensure(_out);
        _paths.Profile.Returns(Profile);
        _paths.TemplatesRoot.Returns(Path.Combine("elsewhere", "t"));
        Sut.Ensure(_out);

        _git.DidNotReceiveWithAnyArgs().Run(default!, default!);
        Assert.Equal("", _out.ToString());
    }

    [Fact]
    public void A_failed_clone_stops_init_with_gits_message()
    {
        _git.Run(Arg.Any<string>(), Arg.Any<string[]>()).Returns(new GitResult(128, "fatal: repository not found"));

        var e = Assert.Throws<FactoryException>(() => Sut.Ensure(_out));

        Assert.Equal("init: could not clone kamabery/template-projects to " + Default + ": fatal: repository not found", e.Message);
    }
}
