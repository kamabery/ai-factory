using Factory.Cli;
using Factory.Core;
using Factory.Store;
using Microsoft.Extensions.Options;

namespace Factory.UnitTests;

public class EditSpecCommandTests : CommandTestBase
{
    private static readonly string Cwd = Path.Combine(Path.GetTempPath(), "cmd-cwd");

    private EditSpecCommand Sut => new(Paths, Files, Options.Create(new FactoryOptions { WorkingDirectory = Cwd }), Clock);

    [Fact]
    public void Replaces_the_spec_and_the_target_relative_to_the_working_directory_and_notes_it()
    {
        var s = FakeSession.With(Jobs.New("J-001", "backlog"));
        Files.Put(Path.Combine(Cwd, "new.md"), "# Spec: Thing\n## Target\n- Repo: o/other\n");

        Sut.Run(Args("edit-spec", "J-001", "-File", "new.md"), s);

        Assert.Equal("# Spec: Thing\n## Target\n- Repo: o/other\n", Files.Text(JobFile("J-001", "spec.md")));
        Assert.Equal(("o/other", Now), ((string?)s.Job("J-001")["target"], (string?)s.Job("J-001")["updatedAt"]));
        Assert.Equal("spec edited", (string?)s.Events.Single()["detail"]);
    }

    [Fact]
    public void A_spec_with_no_target_clears_the_jobs()
    {
        var s = FakeSession.With(Jobs.New("J-001", "backlog"));
        Files.Put(Path.Combine(Cwd, "new.md"), "# Spec: Thing\n");

        Sut.Run(Args("edit-spec", "J-001", "-File", Path.Combine(Cwd, "new.md")), s);

        Assert.Null(s.Job("J-001")["target"]);
    }

    [Theory]
    [InlineData("missing.md")]
    [InlineData(null)]
    public void Refuses_a_file_that_is_not_there(string? file)
    {
        var s = FakeSession.With(Jobs.New("J-001", "backlog"));
        string[] args = file is null ? ["edit-spec", "J-001"] : ["edit-spec", "J-001", "-File", file];

        Assert.Equal($"spec file not found: {file}", Assert.Throws<FactoryException>(() => Sut.Run(Args(args), s)).Message);
        Assert.Equal(0, s.Saves);
    }
}
