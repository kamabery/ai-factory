using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Factory.Core.Abstractions;
using Factory.Store.Abstractions;

namespace Factory.Cli;

/// <summary>
/// An idea session's files under sessions/&lt;S&gt;/ (contract, "Idea sessions"): session.json, chat.json and stream.jsonl.
/// The dashboard reads and writes the same files, so every write is atomic.
/// </summary>
public sealed class IdeaFiles(IFactoryPaths paths, IFileStore files)
{
    internal static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    internal static readonly JsonSerializerOptions Compact = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public string Dir(string session) => Path.Combine(paths.Home, "sessions", session);

    /// <summary>factory/templates: the model may read it beside the target.</summary>
    public string TemplatesDir => Path.GetFullPath(Path.Combine(paths.ScriptsDir, "..", "templates"));

    /// <summary>The repository the factory runs from (the prompt's sources live there).</summary>
    public string FactoryRoot => Path.GetFullPath(Path.Combine(paths.ScriptsDir, "..", ".."));

    public JsonObject? Session(string session) =>
        files.ReadText(Path.Combine(Dir(session), "session.json")) is { } t ? JsonNode.Parse(t) as JsonObject : null;

    public void SaveSession(string session, JsonObject s) => Write(Path.Combine(Dir(session), "session.json"), s);

    public JsonArray Messages(string session) =>
        files.ReadText(Path.Combine(Dir(session), "chat.json")) is { } t && JsonNode.Parse(t)?["messages"] is JsonArray m
            ? (JsonArray)m.DeepClone() : [];

    public void SaveMessages(string session, JsonArray messages) =>
        Write(Path.Combine(Dir(session), "chat.json"), new JsonObject { ["messages"] = messages });

    public void ResetStream(string session) => files.WriteAtomic(Path.Combine(Dir(session), "stream.jsonl"), []);

    /// <summary>One stream.jsonl line: text, tool, end or error.</summary>
    public void Stream(string session, JsonObject line) =>
        files.AppendText(Path.Combine(Dir(session), "stream.jsonl"), line.ToJsonString(Compact) + "\n");

    public string? Text(string path) => files.ReadText(path);

    public void WriteText(string path, string text) => files.WriteAtomic(path, Encoding.UTF8.GetBytes(text));

    private void Write(string path, JsonNode node) => files.WriteAtomic(path, Encoding.UTF8.GetBytes(node.ToJsonString(Indented)));
}
