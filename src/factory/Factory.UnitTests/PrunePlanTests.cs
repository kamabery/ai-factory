using System.Text.Json.Nodes;
using Factory.Core;
using Factory.Store;
using Factory.Store.Abstractions;

namespace Factory.UnitTests;

public class PrunePlanTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static BlobItem Cur(string name, int day = 0, bool leased = false) => new(name, "v-" + name, true, 10, T0.AddDays(day), "e", leased);
    private static BlobItem Old(string name, string version, int day = 0) => new(name, version, false, 10, T0.AddDays(day), "0xV", false);

    private static Job Job(string id, string stage, string updatedAt) =>
        new(new JsonObject { ["id"] = id, ["stage"] = stage, ["updatedAt"] = updatedAt });

    [Fact]
    public void Orders_the_container_as_the_contracts_cap_does()
    {
        var rows = new List<BlobItem> { Cur("board.json"), Cur("events/2026-01.jsonl"), Cur("ideas/I-001/idea.md"), Cur("evals/J-001.md") };
        for (var i = 1; i <= 52; i++) rows.Add(Old("board.json", $"2026-01-01T00:00:{i:D2}Z"));
        rows.Add(Old("jobs/J-001/spec.md", "2026-01-01T00:00:00Z", day: 3));
        rows.Add(Old("ideas/I-001/idea.md", "2026-01-01T00:00:00Z", day: 1));
        rows.AddRange([
            Cur("jobs/J-001/run-1.jsonl"), Cur("jobs/J-001/spec.md"),
            Cur("jobs/J-002/gate.log"), Cur("jobs/J-002/spec.md"),
            Cur("jobs/J-003/run-1.jsonl"),
            Cur("jobs/J-004/transcripts/agent-a.jsonl", day: 9), Cur("jobs/J-004/x.md", day: 2),
            Cur("sessions/S-002/session.json", day: 1), Cur("sessions/S-001/session.json", day: 5), Cur("sessions/S-001/brief.md"),
            Cur("locks/turn-S-002", leased: true),
        ]);
        var jobs = new[]
        {
            Job("J-001", "done", "2026-01-02T00:00:00.000Z"),
            Job("J-002", "discarded", "2026-01-01T00:00:00.000Z"),
            Job("J-003", "build", "2025-01-01T00:00:00.000Z"),
        };

        var order = PrunePlan.Order(rows, jobs).Select(r => r.IsCurrent ? r.Name : $"{r.Name}@{r.VersionId}").ToList();

        Assert.Equal(
        [
            // 1. old versions, oldest first, then board.json's beyond the newest 50 (newest first)
            "ideas/I-001/idea.md@2026-01-01T00:00:00Z", "jobs/J-001/spec.md@2026-01-01T00:00:00Z",
            "board.json@2026-01-01T00:00:02Z", "board.json@2026-01-01T00:00:01Z",
            // 2. finished jobs' bulky logs, oldest job first (J-004 is purged: its newest blob dates it)
            "jobs/J-002/gate.log", "jobs/J-001/run-1.jsonl", "jobs/J-004/transcripts/agent-a.jsonl",
            // 3. the rest of those jobs
            "jobs/J-002/spec.md", "jobs/J-001/spec.md", "jobs/J-004/x.md",
            // 4. sessions whose turn lease is free, oldest session.json first
            "sessions/S-001/session.json", "sessions/S-001/brief.md",
        ], order);
    }

    [Fact]
    public void Never_offers_a_live_job_the_board_events_ideas_evals_or_lessons()
    {
        var rows = new[] { Cur("board.json"), Cur("events/2026-01.jsonl"), Cur("ideas/I-1/a"), Cur("evals/a"), Cur("lessons/a"), Cur("jobs/J-001/run-1.jsonl") };

        Assert.Empty(PrunePlan.Order(rows, [Job("J-001", "approved", "2026-01-01T00:00:00.000Z")]));
    }
}
