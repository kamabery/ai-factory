using Factory.TestGates;

namespace Factory.UnitTests;

/// <summary>The .NET suite's gates (docs/TESTING.md): tier counts, line coverage merged across tiers, 90% Core and 70% elsewhere.</summary>
public class CoverageGatesTests
{
    private static string Cobertura(params (string Package, string File, (int Line, int Hits)[] Lines)[] classes) =>
        "<?xml version=\"1.0\"?><coverage><packages>" + string.Concat(classes.GroupBy(c => c.Package).Select(p =>
            $"<package name=\"{p.Key}\"><classes>" + string.Concat(p.Select(c =>
                $"<class name=\"X\" filename=\"{c.File}\"><lines>" + string.Concat(c.Lines.Select(l => $"<line number=\"{l.Line}\" hits=\"{l.Hits}\" />")) + "</lines></class>"))
            + "</classes></package>")) + "</packages></coverage>";

    [Fact]
    public void A_tiers_counts_are_the_last_total_and_succeeded_lines_of_its_log()
    {
        var log = "Running tests\n  total: 3\n  succeeded: 3\nretry\n  total: 972\n  failed: 1\n  succeeded: 971\n  skipped: 0\n";

        Assert.Equal(new TierCount("unit", 972, 971), CoverageGates.Count("unit", log));
        Assert.Equal(new TierCount("integration", 0, 0), CoverageGates.Count("integration", "no summary"));
    }

    [Fact]
    public void A_line_counts_once_and_is_covered_when_any_tier_hit_it()
    {
        var unit = Cobertura(("Factory.Store", "S.cs", [(1, 1), (2, 0)]));
        var integration = Cobertura(("Factory.Store", "S.cs", [(2, 3), (3, 0)]));

        var rates = CoverageGates.LineRates([unit, integration]);

        Assert.Equal(2.0 / 3, rates["Factory.Store"], 6);
    }

    [Fact]
    public void Excluded_assemblies_test_projects_generated_code_and_migrations_are_not_counted()
    {
        var xml = Cobertura(
            ("Factory.Core", "C.cs", [(1, 1)]),
            ("Factory.Core", "obj/RegexGenerator.g.cs", [(1, 0)]),
            ("Factory.Data", "src/Factory.Data/Migrations/0001_Init.cs", [(1, 0)]),
            ("Factory.Data", "src/Factory.Data/Db.cs", [(1, 1)]),
            ("Factory.TestHost", "T.cs", [(1, 0)]), ("Factory.Simulator", "S.cs", [(1, 0)]), ("Factory.TestGates", "G.cs", [(1, 0)]),
            ("Factory.AppHost", "A.cs", [(1, 0)]), ("Factory.ServiceDefaults", "D.cs", [(1, 0)]), ("Factory.UnitTests", "U.cs", [(1, 0)]));

        var rates = CoverageGates.LineRates([xml]);

        Assert.Equal(["Factory.Core", "Factory.Data"], rates.Keys.Order());
        Assert.All(rates.Values, r => Assert.Equal(1.0, r));
    }

    [Fact]
    public void Core_is_gated_at_90_and_every_other_assembly_at_70()
    {
        var results = CoverageGates.Gate(new Dictionary<string, double> { ["Factory.Core"] = 0.89, ["Factory.Store"] = 0.70, ["Factory.Cli"] = 0.699 });

        Assert.Equal(
        [
            new GateResult("Factory.Cli", 0.699, 0.70, false),
            new GateResult("Factory.Core", 0.89, 0.90, false),
            new GateResult("Factory.Store", 0.70, 0.70, true),
        ], results);
    }

    [Fact]
    public void The_summary_prints_each_tier_with_its_share_and_each_assembly_with_its_gate()
    {
        var summary = CoverageGates.Summary([new TierCount("unit", 80, 80), new TierCount("integration", 20, 19)],
            [new GateResult("Factory.Cli", 0.6, 0.7, false), new GateResult("Factory.Core", 0.95, 0.9, true)], gated: true);

        Assert.Equal(
        [
            "### .NET tests", "", "| Tier | Tests | Passed |", "| --- | --- | --- |", "| unit | 80 (80%) | 80 |", "| integration | 20 (20%) | 19 |",
            "", "| Assembly | Line coverage | Gate |", "| --- | --- | --- |",
            "| Factory.Cli | 60.0% | **FAIL** (< 70%) |", "| Factory.Core | 95.0% | ok (≥ 90%) |",
        ], summary);
    }

    [Fact]
    public void One_tier_alone_is_reported_but_not_gated()
    {
        var summary = CoverageGates.Summary([new TierCount("unit", 5, 5)], [new GateResult("Factory.Cli", 0.1, 0.7, false)], gated: false);

        Assert.Equal("| Factory.Cli | 10.0% | not gated |", summary[^1]);
    }
}
