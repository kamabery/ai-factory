using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Factory.Server.Abstractions;

namespace Factory.Server;

/// <summary>One response, as serve.ps1's Send, Send-Result and Send-File wrote it: a body, or a file to serve.</summary>
public sealed record Reply(int Status, string Body, string Type = "application/json", string? FilePath = null)
{
    internal static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>Send-Result: 200 when factory.ps1 succeeded, else 409, with <c>{ ok, message }</c>.</summary>
    public static Reply Of(CallResult r) => new(r.Ok ? 200 : 409, Message(r.Ok, r.Message));

    public static Reply Error(int status, string message) => new(status, Message(false, message));

    public static Reply NotFound(string message = "not found") => Error(404, message);

    public static Reply BadPath() => Error(400, "bad path");

    public static Reply Data(JsonNode? node) => new(200, node?.ToJsonString(Json) ?? "null");

    public static Reply Raw(string json) => new(200, json);

    /// <summary>Send-File: the file, or 404 when it is not there.</summary>
    public static Reply File(string path) => new(200, "", FilePath: path);

    private static string Message(bool ok, string message) => new JsonObject { ["ok"] = ok, ["message"] = message }.ToJsonString(Json);
}
