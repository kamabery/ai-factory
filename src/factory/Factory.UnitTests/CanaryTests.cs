using System.Text.Json.Nodes;
using Factory.Core;

namespace Factory.UnitTests;

/// <summary>Why a canary is due, whether it is, and how long one takes (contract, "Canary", "Why it is due").</summary>
public class CanaryTests
{
    private static readonly Dictionary<string, string> Now = new()
    {
        ["deploy"] = "d2", ["templates"] = "t2", ["varset"] = "v1", ["settings"] = "s1",
    };

    private static JsonObject Pass(int n, string version, string deploy, string templates, string varset = "v1", string settings = "s1",
        string startedAt = "2026-11-10T12:00:00.000Z", string finishedAt = "2026-11-10T12:15:00.000Z") => new()
    {
        ["n"] = n, ["version"] = version, ["startedAt"] = startedAt, ["finishedAt"] = finishedAt,
        ["inputs"] = new JsonObject { ["deploy"] = deploy, ["templates"] = templates, ["varset"] = varset, ["settings"] = settings },
    };

    private static JsonObject Record(string status, params JsonObject[] passes) => new()
    {
        ["fingerprint"] = "sha256:old", ["status"] = status, ["n"] = passes.Length == 0 ? 4 : (int)passes[0]["n"]!,
        ["passes"] = new JsonArray([.. passes]),
    };

    [Fact]
    public void With_nothing_on_record_the_reason_is_no_pass()
    {
        Assert.Equal(["no pass on record"], Canary.Reasons(null, Now, "0.4.2"));
        Assert.Equal(["no pass on record"], Canary.Reasons(Record("failed"), Now, "0.4.2"));
    }

    [Fact]
    public void A_pass_from_before_spec_22_has_no_groups_to_compare()
    {
        var old = new JsonObject { ["fingerprint"] = "sha256:old", ["status"] = "pass", ["n"] = 32 };
        Assert.Equal(["inputs changed since pass #32"], Canary.Reasons(old, Now, "0.4.2"));
    }

    [Fact]
    public void Each_changed_group_is_named_in_group_order_against_the_newest_pass()
    {
        var rec = Record("failed", Pass(32, "0.4.2", "d1", "t1", "v0", "s0"), Pass(31, "0.4.2", "d2", "t2"));
        Assert.Equal(["deploy code changed since pass #32", "templates changed since pass #32",
            "variable set changed since pass #32", "canary settings changed since pass #32"], Canary.Reasons(rec, Now, "0.4.2"));
        Assert.Equal(["templates changed since pass #32"], Canary.Reasons(Record("pass", Pass(32, "0.4.2", "d2", "t1")), Now, "0.4.2"));
    }

    [Fact]
    public void A_deploy_change_with_a_new_version_names_the_versions()
    {
        Assert.Equal(["factory 0.4.1 → 0.4.2"], Canary.Reasons(Record("pass", Pass(32, "0.4.1", "d1", "t2")), Now, "0.4.2"));
    }

    [Theory]
    [InlineData(null, true, "sha256:now", true)]
    [InlineData("pass", true, "sha256:now", false)]
    [InlineData("pass", true, "sha256:old", true)]
    [InlineData("failed", true, "sha256:now", true)]
    [InlineData("retry", true, "sha256:now", true)]
    [InlineData("running", true, "sha256:old", false)]
    [InlineData(null, false, "sha256:now", false)]
    public void Due_when_deploys_are_on_nothing_runs_and_no_pass_is_recorded_for_the_fingerprint(string? status, bool deploysOn, string recorded, bool due)
    {
        var rec = status is null ? null : new JsonObject { ["fingerprint"] = recorded, ["status"] = status };
        var computed = 0;
        Assert.Equal(due, Canary.Due(rec, () => { computed++; return "sha256:now"; }, deploysOn));
        if (!deploysOn) Assert.Equal(0, computed);
    }

    [Fact]
    public void Typical_minutes_is_15_until_three_passes_then_their_median()
    {
        Assert.Equal(15, Canary.TypicalMinutes(null));
        Assert.Equal(15, Canary.TypicalMinutes(Record("pass", Pass(2, "1", "d", "t", finishedAt: "2026-11-10T12:40:00.000Z"), Pass(1, "1", "d", "t"))));
        Assert.Equal(20, Canary.TypicalMinutes(Record("pass",
            Pass(3, "1", "d", "t", finishedAt: "2026-11-10T12:40:00.000Z"),
            Pass(2, "1", "d", "t", finishedAt: "2026-11-10T12:20:30.000Z"),
            Pass(1, "1", "d", "t", finishedAt: "2026-11-10T12:12:00.000Z"))));
    }
}
