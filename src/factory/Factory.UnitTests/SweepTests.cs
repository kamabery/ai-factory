using Factory.Core;

namespace Factory.UnitTests;

public class SweepTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("2026-11-10T11:00:00.000Z", true)]
    [InlineData("2026-11-10T10:00:00.000Z", false)]
    public void A_destroy_is_running_until_it_is_older_than_the_stale_minutes(string? startedAt, bool running) =>
        Assert.Equal(running, Sweep.DestroyRunning(startedAt, Now, 90));

    [Theory]
    [InlineData(null, null, 0, false)]
    [InlineData("down", null, 0, false)]
    [InlineData("DOWN", null, 0, false)]
    [InlineData("up", null, 0, true)]
    [InlineData("failed", null, 4, true)]
    [InlineData("failed", null, 5, false)]
    [InlineData("destroying", "2026-11-10T11:59:00.000Z", 1, false)]
    [InlineData("destroying", "2026-11-10T09:00:00.000Z", 1, true)]
    public void Due_skips_down_the_fifth_failure_and_a_destroy_that_may_still_run(string? status, string? startedAt, int failures, bool due) =>
        Assert.Equal(due, Sweep.Due(status, startedAt, failures, Now, 90));
}
