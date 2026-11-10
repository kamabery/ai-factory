using Factory.Core;

namespace Factory.UnitTests;

public class StatusReportTests
{
    [Fact]
    public void An_empty_board_says_so()
    {
        Assert.Equal(["board is empty"], StatusReport.Lines(Board.Empty()));
    }

    [Fact]
    public void A_board_with_no_jobs_key_is_empty()
    {
        Assert.Equal(["board is empty"], StatusReport.Lines(Board.Parse("{\"version\":2}")));
    }

    [Fact]
    public void Lists_open_ideas_then_live_jobs_in_factory_ps1_columns()
    {
        var board = Board.Parse("""
            {"ideas":[
              {"id":"I-001","status":"drafting","text":"find books fast"},
              {"id":"I-002","status":"done","text":"hidden"}],
             "jobs":[
              {"id":"J-001","stage":"backlog","round":1,"title":"Search","flag":null},
              {"id":"J-002","stage":"discarded","round":2,"title":"hidden"},
              {"id":"J-010","stage":"ready","round":12,"title":"Merge it","flag":{"reason":"base moved"}}]}
            """);

        Assert.Equal([
            "I-001  idea:drafting find books fast",
            "J-001  backlog    r1 Search",
            "J-010  ready      r12 Merge it [base moved]",
        ], StatusReport.Lines(board));
    }

    [Fact]
    public void Prints_nothing_when_every_idea_is_done_and_there_are_no_jobs()
    {
        // factory.ps1 tests "no jobs and no ideas" before it filters the done ideas out.
        Assert.Empty(StatusReport.Lines(Board.Parse("{\"ideas\":[{\"id\":\"I-001\",\"status\":\"done\",\"text\":\"x\"}],\"jobs\":[]}")));
    }

    [Fact]
    public void Missing_fields_print_as_empty()
    {
        Assert.Equal(["J-001  build      r "], StatusReport.Lines(Board.Parse("{\"jobs\":[{\"id\":\"J-001\",\"stage\":\"build\"}]}")));
    }

    [Fact]
    public void A_long_id_or_stage_pushes_the_columns_right_as_format_does()
    {
        Assert.Equal(["J-10000 approve-wait r3 T"],
            StatusReport.Lines(Board.Parse("{\"jobs\":[{\"id\":\"J-10000\",\"stage\":\"approve-wait\",\"round\":3,\"title\":\"T\"}]}")));
    }
}
