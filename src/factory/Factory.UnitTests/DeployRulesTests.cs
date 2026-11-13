using System.Text.Json.Nodes;
using Factory.Core;

namespace Factory.UnitTests;

public class DeployRulesTests
{
    private static Job InDeploy(string? status) =>
        new(Jobs.New("J-001", "deploy").With("deploy", status is null ? null : new JsonObject { ["status"] = status }));

    [Fact]
    public void RequireFailedDeploy_needs_the_deploy_stage_and_a_failed_attempt()
    {
        DeployRules.RequireFailedDeploy(InDeploy("failed"), "block");
        Assert.Equal("block refused: J-001 is in 'approved', needs deploy",
            Assert.Throws<FactoryException>(() => DeployRules.RequireFailedDeploy(new Job(Jobs.New("J-001", "approved")), "block")).Message);
        Assert.Equal("deploy-fix refused: J-001 has no failed deploy attempt",
            Assert.Throws<FactoryException>(() => DeployRules.RequireFailedDeploy(InDeploy("up"), "deploy-fix")).Message);
        Assert.Throws<FactoryException>(() => DeployRules.RequireFailedDeploy(InDeploy(null), "deploy-fix"));
    }

    private static Job Parked(string stage, string? from, string? reason)
    {
        var j = Jobs.New("J-001", stage).With("flag", reason is null ? null : new JsonObject { ["reason"] = reason });
        if (from is not null) j["history"] = new JsonArray(new JsonObject { ["from"] = "build", ["to"] = from }, new JsonObject { ["from"] = from, ["to"] = stage });
        return new Job(j);
    }

    [Theory]
    [InlineData("blocked", "deploy", "environment")]
    [InlineData("blocked", "deploy", "deploy-failed")]
    [InlineData("ready", "deploy", "deploy-failed")]
    [InlineData("ready", "deploy", "escalated")]
    [InlineData("ready", "review", "github-permission")]
    public void RequireRedeployable_takes_a_job_that_left_deploy_or_lacks_a_permission(string stage, string from, string reason) =>
        DeployRules.RequireRedeployable(Parked(stage, from, reason));

    [Fact]
    public void RequireRedeployable_refuses_the_rest()
    {
        Assert.Equal("redeploy refused: J-001 was blocked from review, not deploy; it never had a passing round to deploy (rework or discard it)",
            Assert.Throws<FactoryException>(() => DeployRules.RequireRedeployable(Parked("blocked", "review", "escalated"))).Message);
        Assert.Equal("redeploy refused: J-001 was blocked from , not deploy; it never had a passing round to deploy (rework or discard it)",
            Assert.Throws<FactoryException>(() => DeployRules.RequireRedeployable(Parked("blocked", null, "escalated"))).Message);
        Assert.Equal("redeploy refused: J-001 is neither flagged github-permission nor came to ready from deploy (deploy-failed or escalated)",
            Assert.Throws<FactoryException>(() => DeployRules.RequireRedeployable(Parked("ready", "gate", "gate-failed"))).Message);
        Assert.Equal("redeploy refused: J-001 is neither flagged github-permission nor came to ready from deploy (deploy-failed or escalated)",
            Assert.Throws<FactoryException>(() => DeployRules.RequireRedeployable(Parked("ready", "deploy", null))).Message);
        Assert.Equal("redeploy refused: J-001 is in 'approved', needs ready|blocked",
            Assert.Throws<FactoryException>(() => DeployRules.RequireRedeployable(Parked("approved", null, null))).Message);
    }
}
