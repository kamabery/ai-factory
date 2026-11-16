using Factory.Store;

namespace Factory.UnitTests;

public class SyncRulesTests
{
    [Theory]
    [InlineData("ideas/I-001/idea.md", true)]
    [InlineData("jobs/J-001/run-1.jsonl", true)]
    [InlineData("sessions/S-001/session.json", true)]
    [InlineData("board.json", false)]
    [InlineData("BOARD.json", false)]
    [InlineData("events.jsonl", false)]
    [InlineData("headless-settings.json", false)]
    [InlineData(".export.json", false)]
    [InlineData(".sync.json", false)]
    [InlineData("jobs/J-001/spec.md.123.tmp", false)]
    [InlineData("jobs/J-001/x.lock", false)]
    [InlineData("events/2026-11.jsonl", false)]
    [InlineData("locks/board", false)]
    [InlineData(".local/storage-token.json", false)]
    public void Syncs_everything_but_the_contracts_exclusions(string rel, bool synced) =>
        Assert.Equal(synced, SyncRules.IsSynced(rel));

    [Theory]
    [InlineData("jobs/J-001/run-1.jsonl", "J-001")]
    [InlineData("jobs/J-001/run-fix-2.jsonl", "J-001")]
    [InlineData("JOBS/J-002/RUN-1.JSONL", "J-002")]
    [InlineData("jobs/J-001/round-1/run-1.jsonl", null)]
    [InlineData("jobs/J-001/gate.log", null)]
    public void Knows_a_jobs_run_log(string rel, string? job) => Assert.Equal(job, SyncRules.RunLogJob(rel));
}
