using System.Text.Json.Nodes;
using Factory.Cli;
using Factory.Core;
using Factory.Core.Abstractions;
using Factory.Store.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Factory.UnitTests;

public class TargetCommandsTests
{
    private readonly IGitHub _gh = Substitute.For<IGitHub>();
    private readonly IGitClient _git = Substitute.For<IGitClient>();
    private readonly MemoryFiles _files = new();
    private static readonly string Dir = Path.Combine(Path.GetTempPath(), "clones", "app");

    public TargetCommandsTests()
    {
        _gh.CloneUrl(Arg.Any<string>()).Returns(c => $"https://gh/{c.Arg<string>()}.git");
        _gh.GitAuth().Returns(["-c", "auth"]);
        _git.Run(Arg.Any<string>(), Arg.Any<string[]>()).Returns(new GitResult(0, ""));
    }

    private static CommandArgs Args(params string[] a) => CommandArgs.Parse(a);

    [Theory]
    [InlineData(RepoState.Existing, "existing")]
    [InlineData(RepoState.Missing, "new")]
    [InlineData(RepoState.Empty, "new")]
    public void CheckTarget_prints_new_or_existing_and_writes_no_board(RepoState state, string printed)
    {
        _gh.Lookup("o/r").Returns(state);
        var s = FakeSession.With();

        Assert.Equal([printed], new CheckTargetCommand(_gh).Run(Args("check-target", "o/r"), s).Out);
        Assert.Equal(0, s.Saves);
    }

    [Fact]
    public void CheckTarget_refuses_a_non_target_and_a_GitHub_error()
    {
        _gh.Lookup("o/r").Throws(new FactoryException("GitHub 0: GITHUB_TOKEN is not set in .env"));
        var sut = new CheckTargetCommand(_gh);

        Assert.Equal(@"check-target refused: target 'D:\code' is not a GitHub repo written owner/name",
            Assert.Throws<FactoryException>(() => sut.Run(Args("check-target", @"D:\code"), FakeSession.With())).Message);
        Assert.Equal("check-target refused: GitHub 0: GITHUB_TOKEN is not set in .env",
            Assert.Throws<FactoryException>(() => sut.Run(Args("check-target", "o/r"), FakeSession.With())).Message);
    }

    [Fact]
    public void ListRepos_prints_a_compact_JSON_array()
    {
        var sut = new ListReposCommand(_gh);
        _gh.OwnedRepos().Returns(["o/a", "o/b"]);
        Assert.Equal(["""["o/a","o/b"]"""], sut.Run(Args("list-repos"), FakeSession.With()).Out);

        _gh.OwnedRepos().Returns([]);
        Assert.Equal(["[]"], sut.Run(Args("list-repos"), FakeSession.With()).Out);
    }

    [Fact]
    public void ListRepos_refuses_a_GitHub_error()
    {
        _gh.OwnedRepos().Throws(new FactoryException("GitHub 401: Bad credentials"));

        Assert.Equal("list-repos refused: GitHub 401: Bad credentials",
            Assert.Throws<FactoryException>(() => new ListReposCommand(_gh).Run(Args("list-repos"), FakeSession.With())).Message);
    }

    private CloneTargetCommand Clone => new(_gh, _git, _files);

    [Fact]
    public void CloneTarget_replaces_the_folder_with_a_shallow_authenticated_clone_of_main()
    {
        _files.Put(Path.Combine(Dir, "stale.txt"), "old");

        var r = Clone.Run(Args("clone-target", "o/r", Dir), FakeSession.With());

        Assert.Equal([Dir], r.Out);
        Assert.Null(_files.Text(Path.Combine(Dir, "stale.txt")));
        Assert.Contains(Path.GetDirectoryName(Dir)!, _files.Directories);
        _git.Received(1).Run(Path.GetDirectoryName(Dir)!, Arg.Is<string[]>(a =>
            a.SequenceEqual(new[] { "-c", "auth", "clone", "-q", "--depth", "1", "--branch", "main", "https://gh/o/r.git", Dir })));
    }

    [Fact]
    public void CloneTarget_with_a_base_clones_its_branch()
    {
        var s = FakeSession.With(Jobs.New("J-004", "ready").With("pr", new JsonObject { ["number"] = 4 }));

        Clone.Run(Args("clone-target", "o/r", Dir, "-Base", "J-004"), s);

        _git.Received(1).Run(Arg.Any<string>(), Arg.Is<string[]>(a => a.Contains("factory/J-004")));
    }

    [Fact]
    public void CloneTarget_ignores_a_folder_it_cannot_delete()
    {
        _files.Locked.Add(Dir);

        Assert.Equal([Dir], Clone.Run(Args("clone-target", "o/r", Dir), FakeSession.With()).Out);
    }

    [Fact]
    public void CloneTarget_refuses_a_failed_clone_with_gits_output()
    {
        _git.Run(Arg.Any<string>(), Arg.Any<string[]>()).Returns(new GitResult(128, "fatal: repository not found"));

        Assert.Equal("clone of o/r failed: fatal: repository not found",
            Assert.Throws<FactoryException>(() => Clone.Run(Args("clone-target", "o/r", Dir), FakeSession.With())).Message);
    }

    [Theory]
    [InlineData(new[] { "clone-target", "nope", "X" }, "clone-target refused: target 'nope' is not a GitHub repo written owner/name")]
    [InlineData(new[] { "clone-target", "o/r" }, "usage: clone-target <owner/name> <absolute dir> [-Base <J-NNN>]")]
    [InlineData(new[] { "clone-target", "o/r", "relative/dir" }, "usage: clone-target <owner/name> <absolute dir> [-Base <J-NNN>]")]
    public void CloneTarget_refuses_bad_arguments(string[] args, string refusal)
    {
        Assert.Equal(refusal, Assert.Throws<FactoryException>(() => Clone.Run(Args(args), FakeSession.With())).Message);
        _git.DidNotReceiveWithAnyArgs().Run(default!, default!);
    }

    [Fact]
    public void CloneTarget_refuses_a_bad_base()
    {
        Assert.Equal("base refused: no job J-009",
            Assert.Throws<FactoryException>(() => Clone.Run(Args("clone-target", "o/r", Dir, "-Base", "J-009"), FakeSession.With())).Message);
    }
}
