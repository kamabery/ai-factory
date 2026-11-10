using Factory.Core;

namespace Factory.UnitTests;

public class BoardTests
{
    [Fact]
    public void An_empty_board_is_version_ideas_jobs_as_the_contract_fixes_it()
    {
        Assert.Equal("{\n  \"version\": 2,\n  \"ideas\": [],\n  \"jobs\": []\n}", PsJson.Write(Board.Empty(), "\n"));
    }

    [Fact]
    public void Parse_adds_ideas_last_when_the_file_has_none()
    {
        Assert.Equal("{\n  \"version\": 2,\n  \"jobs\": [],\n  \"ideas\": []\n}",
            PsJson.Write(Board.Parse("{\"version\":2,\"jobs\":[]}"), "\n"));
    }

    [Fact]
    public void Parse_keeps_the_file_order()
    {
        Assert.Equal(["ideas", "version", "jobs"], Board.Parse("{\"ideas\":[],\"version\":2,\"jobs\":[]}").Select(p => p.Key));
    }

    [Fact]
    public void Parse_refuses_a_board_that_is_not_an_object()
    {
        var e = Assert.Throws<FactoryException>(() => Board.Parse("[]"));
        Assert.Equal("board.json is not a JSON object", e.Message);
    }
}
