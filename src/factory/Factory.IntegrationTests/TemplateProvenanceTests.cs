using System.Text.Json.Nodes;
using Factory.Core;
using Factory.Store;
using Microsoft.Extensions.Options;

namespace Factory.IntegrationTests;

/// <summary>Build check 3 against a real clone: which of a round's changes touch template-sourced files.</summary>
public sealed class TemplateProvenanceTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private (TemplateProvenance Sut, GitRepo Clone) Setup()
    {
        var clone = new GitRepo(Path.Combine(_dir["work"], "jobs", "J-002"));
        var paths = new FactoryPaths(Options.Create(new FactoryOptions { Home = _dir["home"], Work = _dir["work"] }), new GitClient(new ProcessRunner()));
        return (new TemplateProvenance(paths, new FileStore(), new GitClient(new ProcessRunner())), clone);
    }

    private static JsonObject Board(params JsonObject[] jobs) => new() { ["jobs"] = new JsonArray([.. jobs]) };

    private static Job Round(string roundStart) =>
        new(new JsonObject { ["id"] = "J-002", ["target"] = "o/r", ["round"] = 2, ["roundStart"] = roundStart });

    [Fact]
    public void Finds_the_rounds_changes_to_files_a_scaffold_or_addon_commit_added()
    {
        var (sut, clone) = Setup();
        clone.Commit("scaffold: aspire-base", ("Program.cs", "1"), ("IaC/main.tf", "1"));
        clone.Commit("addon: ui-react", ("ui/App.tsx", "1"));
        var start = clone.Commit("feat: round 1", ("Feature.cs", "1"));
        clone.Commit("round 2", ("Program.cs", "2"), ("ui/App.tsx", "2"), ("Feature.cs", "2"), ("New.cs", "1"));

        var p = sut.For(Board(), Round(start));

        Assert.Equal(["Program.cs", "ui/App.tsx"], p.Paths);
        Assert.Equal("no bootstrap merge on record for o/r: only scaffold: and addon: commits count as template-sourced", p.Note);
    }

    [Fact]
    public void A_file_the_targets_bootstrap_merge_brought_counts_whatever_its_commit_says()
    {
        var (sut, clone) = Setup();
        clone.Commit("init", ("README.md", "1"));
        var boot = clone.Commit("bootstrap: the whole scaffold", ("Api.cs", "1"));
        var start = clone.Commit("feat", ("Other.cs", "1"));
        clone.Commit("round 2", ("Api.cs", "2"), ("README.md", "2"));
        var bootstrap = new JsonObject { ["id"] = "J-001", ["kind"] = "bootstrap", ["target"] = "o/r", ["merged"] = new JsonObject { ["sha"] = boot } };

        var p = sut.For(Board(bootstrap), Round(start));

        Assert.Equal(["Api.cs", "README.md"], p.Paths);
        Assert.Null(p.Note);
    }

    [Fact]
    public void A_purged_bootstraps_merge_is_read_from_its_eval()
    {
        var (sut, clone) = Setup();
        var boot = clone.Commit("bootstrap", ("Api.cs", "1"));
        var start = clone.Commit("feat", ("Other.cs", "1"));
        clone.Commit("round 2", ("Api.cs", "2"));
        Directory.CreateDirectory(Path.Combine(_dir["home"], "evals"));
        File.WriteAllText(Path.Combine(_dir["home"], "evals", "J-001.json"), $$"""{"kind":"bootstrap","target":"o/r","merged":"{{boot}}"}""");

        Assert.Equal(["Api.cs"], sut.For(Board(), Round(start)).Paths);
    }

    [Fact]
    public void No_round_start_or_no_clone_is_not_checked()
    {
        var (sut, _) = Setup();

        Assert.Null(sut.For(Board(), new Job(new JsonObject { ["id"] = "J-002" })).Paths);
        Assert.Equal("no round start recorded for J-009", sut.For(Board(), new Job(new JsonObject { ["id"] = "J-009", ["roundStart"] = "abc" })).Note);
    }
}
