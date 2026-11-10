using System.Text.Json.Nodes;
using Factory.Core;

namespace Factory.UnitTests;

public class PsJsonTests
{
    [Fact]
    public void Writes_what_ConvertTo_Json_writes()
    {
        // The input and expected text are PowerShell 7.6's own: ConvertFrom-Json -AsHashtable | ConvertTo-Json -Depth 20.
        var node = JsonNode.Parse("""
            {"a":"x—y <>&' é 😀","t":"l1\nl2\tz\u0001\u001f\u0085\u2028\u007f","n":[],"o":{},"d":0.38,"f":1.0,"i":5,"b":true,"z":null,"e":1E+20,"arr":[1,{"k":[]}]}
            """);

        var json = PsJson.Write(node, "\r\n");

        Assert.Equal(string.Join("\r\n",
            "{",
            "  \"a\": \"x—y <>&' é 😀\",",
            "  \"t\": \"l1\\nl2\\tz\\u0001\\u001f\\u0085\\u2028\u007f\",",
            "  \"n\": [],",
            "  \"o\": {},",
            "  \"d\": 0.38,",
            "  \"f\": 1.0,",
            "  \"i\": 5,",
            "  \"b\": true,",
            "  \"z\": null,",
            "  \"e\": 1E+20,",
            "  \"arr\": [",
            "    1,",
            "    {",
            "      \"k\": []",
            "    }",
            "  ]",
            "}"), json);
    }

    [Theory]
    [InlineData("\b\f\r\u2029\"\\","\"\\b\\f\\r\\u2029\\\"\\\\\"")]
    [InlineData("false", "\"false\"")]
    public void Escapes_strings_as_ConvertTo_Json_does(string value, string expected)
    {
        Assert.Equal(expected, PsJson.Write(JsonValue.Create(value), "\n"));
    }

    [Fact]
    public void Writes_false_and_a_top_level_null()
    {
        Assert.Equal("false", PsJson.Write(JsonValue.Create(false), "\n"));
        Assert.Equal("null", PsJson.Write(null, "\n"));
    }

    [Fact]
    public void Escapes_keys_too()
    {
        Assert.Equal("{\n  \"a\\\"b\": 1\n}", PsJson.Write(JsonNode.Parse("{\"a\\\"b\":1}"), "\n"));
    }
}
