using System.Text.Json.Nodes;
using Factory.Cli;

namespace Factory.UnitTests;

public class PurgeCommandTests : CommandTestBase
{
    private PurgeCommand Sut => new(Paths, Files, Clock);

    private FakeSession Board()
    {
        var s = FakeSession.With(Jobs.New("J-001", "done"), Jobs.New("J-002", "discarded"), Jobs.New("J-003", "done"), Jobs.New("J-004", "build"));
        s.Board["ideas"] = new JsonArray(
            new JsonObject { ["id"] = "I-001", ["status"] = "done", ["jobs"] = new JsonArray("J-001", "J-002") },
            new JsonObject { ["id"] = "I-002", ["status"] = "done", ["jobs"] = new JsonArray("J-003") },
            new JsonObject { ["id"] = "I-003", ["status"] = "drafting", ["jobs"] = new JsonArray() });
        return s;
    }

    [Fact]
    public void Removes_evaluated_finished_jobs_and_ideas_they_leave_empty_and_prints_them()
    {
        var s = Board();
        foreach (var id in new[] { "J-001", "J-002" }) Files.Put(Path.Combine(Home, "evals", $"{id}.json"), "{}");
        Files.Put(JobFile("J-001", "spec.md"), "x");
        Files.Put(Path.Combine(Work, "jobs", "J-001", "README.md"), "clone");
        Files.Put(Path.Combine(Home, "ideas", "I-001", "idea.md"), "x");

        var r = Sut.Run(Args("purge"), s);

        Assert.Equal(["I-001", "J-001", "J-002"], r.Out);
        Assert.Equal(["J-003", "J-004"], s.Board["jobs"]!.AsArray().Select(j => (string?)j!["id"]));
        Assert.Equal(["I-002", "I-003"], s.Board["ideas"]!.AsArray().Select(i => (string?)i!["id"]));
        Assert.Equal((4, 3), ((int)s.Board["seq"]!["J"]!, (int)s.Board["seq"]!["I"]!));
        Assert.Null(Files.Text(JobFile("J-001", "spec.md")));
        Assert.Null(Files.Text(Path.Combine(Work, "jobs", "J-001", "README.md")));
        Assert.Null(Files.Text(Path.Combine(Home, "ideas", "I-001", "idea.md")));
        Assert.Equal("purged I-001, J-001, J-002", (string?)s.Events.Single()["detail"]);
    }

    [Fact]
    public void Keeps_a_purged_jobs_evaluation_and_lessons()
    {
        var s = Board();
        foreach (var f in new[] { "evals/J-001.json", "evals/J-001.md", "evals/J-002.json", "lessons/J-001.json", "lessons/J-001.md" })
            Files.Put(Path.Combine([Home, .. f.Split('/')]), "{}");

        Sut.Run(Args("purge"), s);

        Assert.DoesNotContain("J-001", s.Board["jobs"]!.AsArray().Select(j => (string?)j!["id"]));
        foreach (var f in new[] { "evals/J-001.json", "evals/J-001.md", "lessons/J-001.json", "lessons/J-001.md" })
            Assert.NotNull(Files.Text(Path.Combine([Home, .. f.Split('/')])));
    }

    [Fact]
    public void An_unevaluated_job_stays_and_with_nothing_to_purge_nothing_is_saved()
    {
        var s = Board();

        Assert.Empty(Sut.Run(Args("purge"), s).Out);

        Assert.Equal(0, s.Saves);
        Assert.Empty(s.Events);
    }

    [Fact]
    public void A_locked_work_dir_is_a_note_and_the_purge_goes_on()
    {
        var s = Board();
        Files.Put(Path.Combine(Home, "evals", "J-003.json"), "{}");
        Files.Put(Path.Combine(Work, "jobs", "J-003", "x"), "x");
        Files.Locked.Add(Path.Combine(Work, "jobs", "J-003"));

        Assert.Equal(["I-002", "J-003"], Sut.Run(Args("purge"), s).Out);

        Assert.StartsWith("work dir not removed: ", (string?)s.Events[0]["detail"]);
        Assert.Equal("J-003", (string?)s.Events[0]["job"]);
    }
}
