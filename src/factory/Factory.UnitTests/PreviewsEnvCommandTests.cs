using Factory.Cli;
using Factory.Core.Abstractions;
using Factory.Store.Abstractions;
using NSubstitute;

namespace Factory.UnitTests;

/// <summary><c>factory previews-env</c>: the one-time preview setup's Terraform outputs into .env (contract, "Deploy").</summary>
public class PreviewsEnvCommandTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "previews-env-fx");
    private static readonly string Scripts = Path.Combine(Root, "factory", "scripts");
    private static readonly string Infra = Path.GetFullPath(Path.Combine(Root, "factory", "infra", "previews"));
    private static readonly string EnvPath = Path.Combine(Root, ".env");
    private static readonly string Cwd = Path.Combine(Root, "cwd");
    private readonly MemoryFiles _files = new();
    private readonly IFactoryPaths _paths = Substitute.For<IFactoryPaths>();
    private readonly IProcessRunner _proc = Substitute.For<IProcessRunner>();

    private const string Outputs = """
        {"dotenv":{"sensitive":false,"type":["map","string"],"value":{
          "AZURE_GH_CLIENT_ID":"gh-app","AZURE_TENANT_ID":"tenant","AZURE_SUBSCRIPTION_ID":"sub","HCP_AZURE_VARSET":"varset-1",
          "FACTORY_ENTRA_TENANT_ID":"ext","FACTORY_ENTRA_SUBDOMAIN":"kamfactorypv","FACTORY_ENTRA_USER_FLOW_ID":"flow",
          "HCP_ORG":"never-written","OTHER":"ignored"}},
         "something_else":{"sensitive":false,"type":"string","value":"x"}}
        """;

    public PreviewsEnvCommandTests()
    {
        _paths.ScriptsDir.Returns(Scripts);
        _paths.EnvFile.Returns(EnvPath);
        _proc.Run("terraform", Arg.Any<IReadOnlyList<string>>(), Arg.Any<string>()).Returns(new ProcessResult(0, Outputs, ""));
    }

    private PreviewsEnvCommand Sut() =>
        new(new CliEnvironment(new Dictionary<string, string?>(), Cwd, "\n", "/appdata", "/tmp", "/bin"), _paths, _proc, _files, new EnvSettings(_files));

    private (int Code, string Out, string Err) Run(params string[] args)
    {
        var (o, e) = (new StringWriter { NewLine = "\n" }, new StringWriter { NewLine = "\n" });
        var code = Sut().Run(args, TextReader.Null, o, e);
        return (code, o.ToString(), e.ToString());
    }

    [Fact]
    public void Is_named_previews_env()
    {
        Assert.Equal("previews-env", Sut().Name);
    }

    [Fact]
    public void Writes_each_setting_from_terraforms_dotenv_output_and_never_HCP_ORG()
    {
        _files.Put(EnvPath, "# HCP_ORG=off\nAZURE_TENANT_ID=tenant\n# AZURE_GH_CLIENT_ID=old\nGITHUB_OWNER=me\n");

        var r = Run();

        Assert.Equal((0, """
            AZURE_GH_CLIENT_ID: set
            AZURE_TENANT_ID: unchanged
            AZURE_SUBSCRIPTION_ID: set
            HCP_AZURE_VARSET: set
            FACTORY_ENTRA_TENANT_ID: set
            FACTORY_ENTRA_SUBDOMAIN: set
            FACTORY_ENTRA_USER_FLOW_ID: set

            """.ReplaceLineEndings("\n"), ""), r);
        Assert.Equal(string.Join(Environment.NewLine,
            "# HCP_ORG=off", "AZURE_TENANT_ID=tenant", "AZURE_GH_CLIENT_ID=gh-app", "GITHUB_OWNER=me", "AZURE_SUBSCRIPTION_ID=sub",
            "HCP_AZURE_VARSET=varset-1", "FACTORY_ENTRA_TENANT_ID=ext", "FACTORY_ENTRA_SUBDOMAIN=kamfactorypv", "FACTORY_ENTRA_USER_FLOW_ID=flow") + Environment.NewLine,
            _files.ReadText(EnvPath));
        _proc.Received(1).Run("terraform", Arg.Is<IReadOnlyList<string>>(a => a.SequenceEqual(new[] { $"-chdir={Infra}", "output", "-json" })), Arg.Any<string>());
    }

    [Fact]
    public void A_second_run_changes_nothing()
    {
        Run();
        var once = _files.ReadText(EnvPath);

        var r = Run();

        Assert.Equal(once, _files.ReadText(EnvPath));
        Assert.All(r.Out.TrimEnd('\n').Split('\n'), l => Assert.EndsWith(": unchanged", l));
    }

    [Fact]
    public void From_reads_the_json_file_relative_to_the_working_directory_and_runs_no_terraform()
    {
        _files.Put(Path.Combine(Cwd, "out.json"), """{"dotenv":{"value":{"AZURE_TENANT_ID":"t2"}}}""");

        var r = Run("--from", "out.json");

        Assert.Equal((0, "AZURE_TENANT_ID: set\n", ""), r);
        Assert.Equal("AZURE_TENANT_ID=t2" + Environment.NewLine, _files.ReadText(EnvPath));
        _proc.DidNotReceiveWithAnyArgs().Run(default!, default!, default!);
    }

    [Theory]
    [InlineData("""{"other":{"value":"x"}}""", "terraform output has no dotenv map")]
    [InlineData("""{"dotenv":{"value":"not a map"}}""", "terraform output has no dotenv map")]
    [InlineData("not json", "terraform output is not JSON")]
    public void Refuses_output_without_a_dotenv_map_and_changes_nothing(string json, string why)
    {
        _files.Put(EnvPath, "A=1\n");
        _proc.Run("terraform", Arg.Any<IReadOnlyList<string>>(), Arg.Any<string>()).Returns(new ProcessResult(0, json, ""));

        Assert.Equal((1, "", $"factory: previews-env refused: {why}; run terraform apply in factory/infra/previews first\n"), Run());
        Assert.Equal("A=1\n", _files.ReadText(EnvPath));
    }

    [Fact]
    public void Refuses_when_terraform_fails_with_its_first_error_line()
    {
        _proc.Run("terraform", Arg.Any<IReadOnlyList<string>>(), Arg.Any<string>())
            .Returns(new ProcessResult(1, "", "╷\n│ Error: No outputs found\n│ \n│ The state file either has no outputs defined.\n╵\n"));

        Assert.Equal((1, "", "factory: previews-env refused: terraform output failed: Error: No outputs found; run terraform apply in factory/infra/previews first\n"), Run());
    }

    [Fact]
    public void Refuses_a_missing_from_file_and_an_unknown_argument()
    {
        Assert.Equal((1, "", $"factory: previews-env refused: {Path.Combine(Cwd, "nope.json")} does not exist; run terraform apply in factory/infra/previews first\n"), Run("--from", "nope.json"));
        Assert.Equal((2, "", "factory: previews-env refused: usage: factory previews-env [--from <file>]\n"), Run("--bogus"));
        Assert.Equal((2, "", "factory: previews-env refused: usage: factory previews-env [--from <file>]\n"), Run("--from"));
    }
}
