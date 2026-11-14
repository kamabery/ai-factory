using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Factory.Core;
using Factory.Core.Abstractions;
using Factory.Store.Abstractions;

namespace Factory.Store;

/// <summary>state.ps1's sync: FACTORY_HOME is the container's write-through cache (contract, "State backends").</summary>
public sealed partial class StateSync(IFactoryPaths paths, IFileStore files, IBlobClient blobs, IStateSettings settings, TimeProvider clock) : IStateSync
{
    private SyncManifest? _manifest;

    private SyncManifest Manifest => _manifest ??= new SyncManifest(files, paths.Home);

    private string Local(string rel) => Path.Combine(paths.Home, rel.Replace('/', Path.DirectorySeparatorChar));

    private string Rel(string full) => Path.GetRelativePath(paths.Home, full).Replace('\\', '/');

    public int Push(Func<string, bool>? skip = null)
    {
        var m = Manifest;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var n = 0;
        foreach (var f in files.Files(paths.Home))
        {
            var rel = Rel(f.Path);
            if (!SyncRules.IsSynced(rel)) continue;
            seen.Add(rel);
            var e = m[rel];
            if (SyncManifest.Pruned(e) || SyncManifest.Unchanged(e, f)) continue;
            if (skip is not null && skip(rel)) continue;
            var bytes = files.ReadBytes(f.Path);
            if (bytes is null) continue;
            var etag = blobs.Write(rel, bytes);
            m.Set(rel, new JsonObject { ["len"] = bytes.Length, ["mtime"] = f.LastWriteTicks, ["etag"] = etag });
            n++;
        }
        foreach (var rel in m.Entries.Select(kv => kv.Key).ToList())
        {
            if (seen.Contains(rel) || !SyncRules.IsSynced(rel)) continue;
            if (!SyncManifest.Pruned(m[rel])) blobs.Delete(rel);
            m.Remove(rel);
        }
        m.Save();
        return n;
    }

    public int Pull(string prefix = "")
    {
        var m = Manifest;
        var n = 0;
        foreach (var b in blobs.List(prefix))
        {
            var rel = b.Name;
            if (!SyncRules.IsSynced(rel)) continue;
            var e = m[rel];
            if (e is not null && string.Equals(SyncManifest.Str(e["etag"]), b.ETag, StringComparison.OrdinalIgnoreCase)) continue;
            var p = Local(rel);
            // Local changes win: a file changed since its last sync, or never synced, is kept.
            if (files.Stat(p) is { } f && !SyncManifest.Unchanged(e, f)) continue;
            if (blobs.Read(rel) is not { Bytes: { } bytes } r) continue;
            files.WriteAtomic(p, bytes);
            m.Set(rel, SyncManifest.Stamp(files.Stat(p)!, r.ETag));
            n++;
        }
        m.Save();
        return n;
    }

    public BoardSync SyncBoard()
    {
        var m = Manifest;
        var e = m["board.json"];
        var r = blobs.Read("board.json", e is null ? null : SyncManifest.Str(e["etag"]));
        if (r is null)
        {
            if (e is not null) throw new FactoryException("state: board.json is gone from the container; restore a version of it, or point FACTORY_BLOB_URL at the right container");
            if (files.Stat(paths.BoardPath) is not null) throw new FactoryException($"state: the container has no board.json but {paths.BoardPath} exists; run factory state-migrate");
            return BoardSync.None;
        }
        if (r.NotModified) return BoardSync.Same;
        files.WriteAtomic(paths.BoardPath, r.Bytes!);
        m.Set("board.json", SyncManifest.Stamp(files.Stat(paths.BoardPath)!, r.ETag));
        m.Save();
        return BoardSync.Changed;
    }

    public void SaveBoard(byte[] bytes)
    {
        var m = Manifest;
        var e = m["board.json"];
        var etag = e is null ? blobs.Write("board.json", bytes, null, true) : blobs.Write("board.json", bytes, SyncManifest.Str(e["etag"]), false);
        files.WriteAtomic(paths.BoardPath, bytes);
        m.Set("board.json", SyncManifest.Stamp(files.Stat(paths.BoardPath)!, etag));
        m.Save();
    }

    public void AppendEvent(string line)
    {
        var bytes = Encoding.UTF8.GetBytes(line + "\n");
        var name = $"events/{clock.GetUtcNow().ToString("yyyy-MM", CultureInfo.InvariantCulture)}.jsonl";
        var m = Manifest;
        var e = m[name];
        var r = blobs.Append(name, bytes);
        // Another machine's block landed in between: leave the ETag unknown, so a reader rebuilds events.jsonl.
        m.Set(name, r.Offset == (e is null ? 0 : SyncManifest.Long(e["len"]))
            ? new JsonObject { ["etag"] = r.ETag, ["len"] = r.Offset + bytes.Length }
            : new JsonObject { ["etag"] = "", ["len"] = 0 });
        m.Set("events/", new JsonObject { ["etag"] = "listed" });
        m.Save();
    }

    public bool SyncEvents() => settings.IsAzure && RefreshEvents(force: false);

    /// <param name="force">Rebuild events.jsonl even when this month's blob has not changed.</param>
    private bool RefreshEvents(bool force)
    {
        var m = Manifest;
        var cur = Month(clock.GetUtcNow());
        var etag = SyncManifest.Str(m[cur]?["etag"]);
        var r = blobs.Read(cur, string.IsNullOrEmpty(etag) ? null : etag);
        if (!force && m["events/"] is not null && (r is null || r.NotModified)) return false;
        var months = blobs.List("events/").OrderBy(b => b.Name, StringComparer.Ordinal).ToList();
        m.Set("events/", new JsonObject { ["etag"] = "listed" });
        if (months.Count == 0) { m.Save(); return false; }
        using var all = new MemoryStream();
        foreach (var b in months)
        {
            if (blobs.Read(b.Name) is not { Bytes: { } bytes } got) continue;
            all.Write(bytes);
            m.Set(b.Name, new JsonObject { ["etag"] = got.ETag, ["len"] = bytes.Length });
        }
        files.WriteAtomic(paths.EventsPath, all.ToArray());
        m.Save();
        return true;
    }

    public int Migrate()
    {
        if (blobs.Read("board.json") is not null) throw new FactoryException("state-migrate refused: the container already has a board.json");
        var m = Manifest;
        var n = 0;
        if (files.ReadText(paths.EventsPath) is { } events)
        {
            var byMonth = new List<(string Month, StringBuilder Lines)>();
            // ReadAllLines: \n, \r\n or \r end a line.
            foreach (var line in events.ReplaceLineEndings("\n").Split('\n'))
            {
                if (line.Trim().Length == 0) continue;
                var month = EventMonth().Match(line) is { Success: true } t ? t.Groups[1].Value : clock.GetUtcNow().ToString("yyyy-MM", CultureInfo.InvariantCulture);
                var slot = byMonth.FindIndex(x => x.Month == month);
                if (slot < 0) { byMonth.Add((month, new StringBuilder())); slot = byMonth.Count - 1; }
                byMonth[slot].Lines.Append(line).Append('\n');
            }
            foreach (var (month, lines) in byMonth)
            {
                var name = $"events/{month}.jsonl";
                blobs.Delete(name);   // a migrate that stopped before the board left this behind
                var bytes = Encoding.UTF8.GetBytes(lines.ToString());
                AppendResult? last = null;
                // An append block is at most 4 MiB.
                for (var at = 0; at < bytes.Length; at += AppendBlock) last = blobs.Append(name, bytes[at..Math.Min(bytes.Length, at + AppendBlock)]);
                m.Set(name, new JsonObject { ["etag"] = last?.ETag, ["len"] = bytes.Length });
                n++;
            }
            m.Set("events/", new JsonObject { ["etag"] = "listed" });
        }
        n += Push();
        if (files.ReadText(paths.BoardPath) is { } board) { SaveBoard(Encoding.UTF8.GetBytes(board)); n++; }
        return n;
    }

    private const int AppendBlock = 4 * 1024 * 1024;

    private static string Month(DateTimeOffset now) => $"events/{now.ToString("yyyy-MM", CultureInfo.InvariantCulture)}.jsonl";

    [GeneratedRegex(@"""ts""\s*:\s*""(\d{4}-\d{2})")] private static partial Regex EventMonth();

    public PruneResult Prune(IEnumerable<Job> jobs)
    {
        var rows = blobs.List("", versions: true);
        var used = rows.Sum(r => r.Length);
        var cap = settings.Cap;
        if (used <= cap) return new PruneResult(used, cap, 0, 0);

        var m = Manifest;
        int deleted = 0;
        long freed = 0;
        foreach (var r in PrunePlan.Order(rows, jobs))
        {
            if (used <= cap) break;
            if (r.IsCurrent)
            {
                blobs.Delete(r.Name);
                // With versioning on, the deleted blob lives on as a version until that is deleted too.
                if (r.VersionId is not null) blobs.Delete(r.Name, r.VersionId);
                var e = m[r.Name];
                if (e is not null) e["pruned"] = true;
                else m.Set(r.Name, new JsonObject { ["pruned"] = true });
            }
            else blobs.Delete(r.Name, r.VersionId);
            used -= r.Length;
            freed += r.Length;
            deleted++;
        }
        m.Save();
        return new PruneResult(used, cap, deleted, freed);
    }

    public void Download()
    {
        var board = blobs.Read("board.json") ?? throw new FactoryException("state-import refused: the container has no board.json");
        files.WriteAtomic(paths.BoardPath, board.Bytes!);
        Manifest.Set("board.json", SyncManifest.Stamp(files.Stat(paths.BoardPath)!, board.ETag));
        Manifest.Save();
        Pull();
        RefreshEvents(force: true);
    }
}
