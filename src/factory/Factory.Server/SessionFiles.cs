using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Factory.Core.Abstractions;
using Factory.Server.Abstractions;
using Factory.Store.Abstractions;
using Microsoft.Extensions.Options;

namespace Factory.Server;

/// <summary>
/// An idea session's files under sessions/&lt;S&gt;/ (contract, "Idea sessions"): session.json, chat.json and human.jsonl,
/// plus its clone under &lt;work&gt;/sessions/&lt;S&gt;. idea-turn.ps1 reads and writes the same files, so writes are atomic.
/// </summary>
public sealed class SessionFiles(IFactoryPaths paths, IFileStore files, IProcessProbe procs, IOptions<ServerOptions> options, TimeProvider clock)
{
    private static readonly JsonSerializerOptions Indented = new(Reply.Json) { WriteIndented = true };

    public string Root => Path.Combine(paths.Home, "sessions");

    public string Dir(string id) => Path.Combine(Root, id);

    /// <summary>The session's shallow clone of its target, for the model's read tools.</summary>
    public string Clone(string id) => Path.Combine(options.Value.WorkRoot, "sessions", id);

    public bool Exists(string id) => files.ReadText(Path.Combine(Dir(id), "session.json")) is not null;

    public JsonObject Read(string id) => (JsonObject)JsonNode.Parse(files.ReadText(Path.Combine(Dir(id), "session.json"))!)!;

    public void Save(JsonObject s) => Write(Path.Combine(Dir(BoardFile.Str(s, "id")!), "session.json"), s);

    public JsonArray ReadChat(string id) =>
        files.ReadText(Path.Combine(Dir(id), "chat.json")) is { } t && JsonNode.Parse(t)?["messages"] is JsonArray m ? (JsonArray)m.DeepClone() : [];

    public void SaveChat(string id, JsonArray messages) => Write(Path.Combine(Dir(id), "chat.json"), new JsonObject { ["messages"] = messages });

    /// <summary>The human's words, verbatim and in order, so an idea can be tried again.</summary>
    public void AddHuman(string id, string kind, string text, JsonObject? extra = null)
    {
        var line = new JsonObject { ["ts"] = clock.GetUtcNow().UtcDateTime.ToString("o"), ["kind"] = kind, ["text"] = text };
        foreach (var (k, v) in extra ?? []) line[k] = v?.DeepClone();
        files.AppendText(Path.Combine(Dir(id), "human.jsonl"), line.ToJsonString(Reply.Json) + "\n");
    }

    /// <summary>
    /// A turn is running while a live process holds turn.lock, or just after the server started one (the process may
    /// not hold the lock yet).
    /// </summary>
    public bool Busy(JsonObject s)
    {
        var id = BoardFile.Str(s, "id")!;
        if (ServerFiles.LockHolder(files, Path.Combine(Dir(id), "turn.lock")) is { } pid && procs.IsAlive(pid)) return true;
        if (BoardFile.Str(s, "status") != "running") return false;
        var stat = files.Stat(Path.Combine(Dir(id), "session.json"));
        return stat is not null && new DateTime(stat.LastWriteTicks, DateTimeKind.Utc) > clock.GetUtcNow().UtcDateTime.AddSeconds(-15);
    }

    private void Write(string path, JsonNode node)
    {
        files.EnsureDirectory(Path.GetDirectoryName(path)!);
        files.WriteAtomic(path, Encoding.UTF8.GetBytes(node.ToJsonString(Indented)));
    }
}
