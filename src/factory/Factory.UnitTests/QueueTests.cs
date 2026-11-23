using System.Text.Json.Nodes;
using Factory.Core;

namespace Factory.UnitTests;

public class QueueTests
{
    static JsonObject BoardOf(params string[] jobs) =>
        Board.Parse($$"""{"version":2,"jobs":[{{string.Join(",", jobs)}}]}""");

    static string J(string id, string stage, string? approvedAt = null, string extra = "") =>
        $$"""{"id":"{{id}}","stage":"{{stage}}","approvedAt":{{(approvedAt is null ? "null" : $"\"{approvedAt}\"")}}{{extra}}}""";

    static readonly Func<CanaryState> NoCanaryCall = () => throw new InvalidOperationException("canary state read");

    [Fact]
    public void Next_is_the_oldest_approved_job_by_approved_at_then_id()
    {
        var b = BoardOf(J("J-003", "approved", "2026-11-01T10:00:00.000Z"), J("J-002", "approved", "2026-11-01T09:00:00.000Z"),
                        J("J-001", "approved", "2026-11-01T09:00:00.000Z"), J("J-004", "backlog"));

        Assert.Equal("J-001", Queue.Next(b, deploysOn: false, NoCanaryCall, needsCanary: false));
        Assert.Equal("J-001", Queue.NextRunnable(b)?.Id);
    }

    [Theory]
    [InlineData("build")]
    [InlineData("review")]
    [InlineData("deploy")]
    [InlineData("approve")]
    [InlineData("gate")]
    public void Nothing_is_next_while_a_job_is_building(string stage)
    {
        var b = BoardOf(J("J-001", "approved", "2026-11-01T09:00:00.000Z"), J("J-002", stage));

        Assert.Null(Queue.Next(b, false, NoCanaryCall, false));
        Assert.Null(Queue.NextRunnable(b));
    }

    [Fact]
    public void A_blocked_job_does_not_hold_the_queue()
    {
        var b = BoardOf(J("J-001", "approved", "2026-11-01T09:00:00.000Z"), J("J-002", "blocked"));

        Assert.Equal("J-001", Queue.Next(b, false, NoCanaryCall, false));
    }

    [Fact]
    public void A_usage_limit_flag_holds_every_job()
    {
        var b = BoardOf(J("J-001", "approved", "2026-11-01T09:00:00.000Z"), J("J-002", "ready", extra: ""","flag":{"reason":"usage-limit","detail":""}"""));

        Assert.Null(Queue.Next(b, false, NoCanaryCall, false));
        Assert.Null(Queue.NextRunnable(b));
    }

    [Theory]
    [InlineData(CanaryState.Pass, false, "J-001")]
    [InlineData(CanaryState.Failed, false, "J-002")]
    [InlineData(CanaryState.None, false, "J-002")]
    [InlineData(CanaryState.None, true, "canary")]
    [InlineData(CanaryState.Failed, true, "J-002")]
    public void A_deployable_job_waits_for_a_canary_pass_and_a_bootstrap_does_not(CanaryState canary, bool needsCanary, string expected)
    {
        var b = BoardOf(J("J-001", "approved", "2026-11-01T09:00:00.000Z"),
                        J("J-002", "approved", "2026-11-01T10:00:00.000Z", ""","kind":"bootstrap" """));

        Assert.Equal(expected, Queue.Next(b, deploysOn: true, () => canary, needsCanary));
    }

    [Theory]
    [InlineData(CanaryState.Pass, true, "J-001")]
    [InlineData(CanaryState.Failed, false, "J-003")]
    [InlineData(CanaryState.Failed, true, "J-003")]
    [InlineData(CanaryState.None, true, "canary")]
    public void Jobs_waiting_in_canary_are_taken_first_by_approval_and_only_after_a_pass(CanaryState canary, bool needsCanary, string expected)
    {
        var b = BoardOf(J("J-002", "approved", "2026-11-01T11:00:00.000Z"),
                        J("J-001", "canary", "2026-11-01T09:00:00.000Z"),
                        J("J-003", "approved", "2026-11-01T12:00:00.000Z", ""","kind":"bootstrap" """));

        Assert.Equal(expected, Queue.Next(b, deploysOn: true, () => canary, needsCanary));
    }

    [Fact]
    public void To_hold_is_every_deployable_approved_job_in_queue_order()
    {
        var b = BoardOf(J("J-002", "approved", "2026-11-01T11:00:00.000Z"), J("J-001", "approved", "2026-11-01T09:00:00.000Z"),
                        J("J-003", "approved", "2026-11-01T08:00:00.000Z", ""","kind":"bootstrap" """), J("J-004", "canary", "2026-11-01T07:00:00.000Z"));

        Assert.Equal(["J-001", "J-002"], Queue.ToHold(b, deploysOn: true).Select(j => j.Id));
        Assert.Empty(Queue.ToHold(b, deploysOn: false));
    }

    [Fact]
    public void The_canary_state_is_read_once_and_only_for_a_deployable_job()
    {
        var calls = 0;
        var b = BoardOf(J("J-001", "approved", "2026-11-01T09:00:00.000Z"), J("J-002", "approved", "2026-11-01T10:00:00.000Z"));

        Assert.Null(Queue.Next(b, true, () => { calls++; return CanaryState.Failed; }, false));
        Assert.Equal(1, calls);
        Assert.Equal("J-001", Queue.Next(b, false, NoCanaryCall, false));
    }

    [Fact]
    public void Is_next_holds_for_the_queue_head_even_before_a_canary_and_for_the_job_next_skips_to()
    {
        var b = BoardOf(J("J-001", "approved", "2026-11-01T09:00:00.000Z"),
                        J("J-002", "approved", "2026-11-01T10:00:00.000Z", ""","kind":"bootstrap" """),
                        J("J-003", "approved", "2026-11-01T11:00:00.000Z"));
        var job = (string id) => b.Job(id);

        Assert.True(Queue.IsNext(b, job("J-001"), true, () => CanaryState.Failed));
        Assert.True(Queue.IsNext(b, job("J-002"), true, () => CanaryState.Failed));
        Assert.False(Queue.IsNext(b, job("J-003"), true, () => CanaryState.Failed));
    }

    [Theory]
    [InlineData(null, "none")]
    [InlineData("""{"fingerprint":"sha256:old","status":"pass"}""", "none")]
    [InlineData("""{"fingerprint":"sha256:now","status":"pass"}""", "pass")]
    [InlineData("""{"fingerprint":"sha256:now","status":"failed"}""", "failed")]
    [InlineData("""{"fingerprint":"sha256:now","status":"running"}""", "none")]
    public void Canary_state_is_the_record_for_the_current_fingerprint(string? json, string expected)
    {
        var c = json is null ? null : JsonNode.Parse(json)!.AsObject();

        Assert.Equal(expected, Canary.State(c, () => "sha256:now").ToString().ToLowerInvariant());
    }

    [Theory]
    [InlineData(null, null, 0, false)]
    [InlineData("down", null, 0, false)]
    [InlineData("up", null, 0, true)]
    [InlineData("failed", null, 4, true)]
    [InlineData("failed", null, 5, false)]
    [InlineData("destroying", "2026-11-10T13:00:00.000Z", 0, false)]
    [InlineData("destroying", "2026-11-10T12:00:00.000Z", 0, true)]
    [InlineData("destroying", null, 0, true)]
    public void Sweep_destroys_what_is_up_unless_a_destroy_is_running_or_failed_five_times(string? status, string? startedAt, int failures, bool due)
    {
        var now = new DateTimeOffset(2026, 11, 10, 14, 0, 0, TimeSpan.Zero);

        Assert.Equal(due, Queue.SweepDue(status, startedAt, failures, now, staleMinutes: 90));
    }
}
