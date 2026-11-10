using System.Text.Json.Nodes;
using Factory.Core;

namespace Factory.UnitTests;

public class MergeRulesTests
{
    static Job WithGate(string gateJson) =>
        new(JsonNode.Parse($$"""{"id":"J-001","stage":"ready","gate":{{gateJson}}}""")!.AsObject());

    [Theory]
    [InlineData("""{"build":0,"test":0,"sha":"abc1234","at":"x"}""", "abc1234", true)]
    [InlineData("""{"build":0,"test":0,"sha":"ABC1234","at":"x"}""", "abc1234", true)]
    [InlineData("""{"build":0,"test":1,"sha":"abc1234","at":"x"}""", "abc1234", false)]
    [InlineData("""{"build":0,"sha":"abc1234","at":"x"}""", "def5678", false)]
    [InlineData("null", "abc1234", false)]
    public void A_gate_is_green_when_every_command_exited_0_at_the_branch_head(string gate, string head, bool green)
    {
        Assert.Equal(green, MergeRules.GateGreen(WithGate(gate), head));
    }

    static JsonObject Stack(string baseStage, bool basePr = true) => Board.Parse($$"""
        {"jobs":[
          {"id":"J-020","stage":"{{baseStage}}","target":"o/app","pr":{{(basePr ? """{"number":3}""" : "null")}}},
          {"id":"J-021","stage":"ready","target":"o/app","base":"J-020"},
          {"id":"J-022","stage":"done","target":"o/app","base":"J-020"},
          {"id":"J-030","stage":"ready","target":"o/app","base":null}]}
        """);

    [Theory]
    [InlineData("ready", "factory/J-020")]
    [InlineData("blocked", "factory/J-020")]
    [InlineData("done", "main")]
    public void A_stacked_job_branches_from_its_base_until_the_base_is_done(string baseStage, string branch)
    {
        var b = Stack(baseStage);

        Assert.Equal(branch, MergeRules.BaseBranch(b, b.Job("J-021")));
        Assert.Equal("main", MergeRules.BaseBranch(b, b.Job("J-030")));
    }

    [Fact]
    public void A_purged_base_counts_as_merged()
    {
        var b = Board.Parse("""{"jobs":[{"id":"J-021","stage":"ready","base":"J-009"}]}""");

        Assert.Null(MergeRules.BaseJob(b, b.Job("J-021")));
        Assert.Equal("main", MergeRules.BaseBranch(b, b.Job("J-021")));
        MergeRules.RequireBaseMerged(b, b.Job("J-021"));
    }

    [Fact]
    public void Merge_refuses_while_the_base_is_unmerged()
    {
        var b = Stack("ready");

        var x = Assert.Throws<FactoryException>(() => MergeRules.RequireBaseMerged(b, b.Job("J-021")));

        Assert.Equal("merge refused: J-021 is stacked on J-020; merge J-020 first", x.Message);
        MergeRules.RequireBaseMerged(Stack("done"), Stack("done").Job("J-021"));
    }

    [Fact]
    public void Discard_refuses_a_job_with_live_stacked_jobs()
    {
        var b = Stack("ready");

        var x = Assert.Throws<FactoryException>(() => MergeRules.RequireNothingStacked(b, b.Job("J-020")));

        Assert.Equal("discard refused: J-021 stacked on J-020; discard them first, or merge J-020", x.Message);
        MergeRules.RequireNothingStacked(b, b.Job("J-021"));
    }

    [Theory]
    [InlineData("J-020", "o/app", null)]
    [InlineData("J-099", "o/app", "no job J-099")]
    [InlineData("J-020", "o/other", "J-020 is on o/app, not o/other")]
    [InlineData("J-022", "o/app", "J-022 is done; start from main")]
    [InlineData("J-021", "o/app", "J-021 has no PR yet, so there is no branch to build on")]
    public void A_base_is_a_live_job_on_the_same_target_with_a_pr(string id, string target, string? refusal)
    {
        Assert.Equal(refusal, MergeRules.BaseError(Stack("ready"), target, id));
    }

    [Theory]
    [InlineData("", "3", null, null)]
    [InlineData("src/a.cs\nsrc/b.cs", "3", "uncommitted changes in the work dir: src/a.cs, src/b.cs", "merge blocked: uncommitted changes")]
    [InlineData("", "0", "nothing to merge: factory/J-001 adds no commits to main", "merge blocked: nothing to merge")]
    [InlineData("", "", "nothing to merge: factory/J-001 adds no commits to main", "merge blocked: nothing to merge")]
    [InlineData("", "03", "nothing to merge: factory/J-001 adds no commits to main", "merge blocked: nothing to merge")]
    public void Merge_needs_a_clean_work_dir_and_commits_beyond_main(string dirty, string ahead, string? detail, string? refusal)
    {
        var block = MergeRules.WorkBlock("J-001", dirty, ahead);

        Assert.Equal(detail, block?.Detail);
        Assert.Equal(refusal, block?.Refusal);
    }
}
