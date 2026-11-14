using System.Text.Json.Nodes;

namespace Factory.TestHost;

/// <summary>A stub's answer: a status, then a JSON body, a text body or raw bytes, and any headers.</summary>
public sealed record StubReply(int Status, JsonNode? Body = null, string? Text = null, string ContentType = "text/plain",
    IReadOnlyDictionary<string, string>? Headers = null, byte[]? Bytes = null)
{
    public static StubReply Json(int status, JsonNode? body) => new(status, body);

    public static StubReply Message(int status, string message) => new(status, new JsonObject { ["message"] = message });
}
