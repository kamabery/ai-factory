using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Factory.Core;
using Factory.Core.Abstractions;

namespace Factory.Store;

/// <summary>
/// .sync.json, the cache's manifest: path -> { len, mtime (UTC ticks), etag, pruned }, what this home last synced.
/// Read once, written after every sync step, as state.ps1 does.
/// </summary>
internal sealed class SyncManifest(IFileStore files, string home)
{
    private readonly string _path = Path.Combine(home, ".sync.json");
    private JsonObject? _entries;

    public JsonObject Entries => _entries ??= Load();

    public JsonObject? this[string rel] => Entries.TryGetPropertyValue(rel, out var e) ? e as JsonObject : null;

    public void Set(string rel, JsonObject entry) => Entries[rel] = entry;

    public void Remove(string rel) => Entries.Remove(rel);

    public void Save() => files.WriteAtomic(_path, Encoding.UTF8.GetBytes(PsJson.Write(Entries, Environment.NewLine)));

    public static JsonObject Stamp(LocalFile f, string? etag) => new() { ["len"] = f.Length, ["mtime"] = f.LastWriteTicks, ["etag"] = etag };

    public static long Long(JsonNode? n) => n is JsonValue v && v.GetValueKind() == JsonValueKind.Number
        ? long.Parse(v.ToJsonString(), NumberStyles.Integer, CultureInfo.InvariantCulture) : 0;

    public static string? Str(JsonNode? n) => n is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : null;

    public static bool Pruned(JsonObject? e) => e?["pruned"] is JsonValue v && v.GetValueKind() == JsonValueKind.True;

    /// <summary>The file is as the manifest last saw it: the same length and write time.</summary>
    public static bool Unchanged(JsonObject? e, LocalFile f) => e is not null && Long(e["len"]) == f.Length && Long(e["mtime"]) == f.LastWriteTicks;

    private JsonObject Load()
    {
        var text = files.ReadBytes(_path);
        if (text is null) return [];
        return JsonNode.Parse(text) as JsonObject ?? [];
    }
}
