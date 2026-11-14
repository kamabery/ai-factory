using System.Text;
using System.Text.Json.Nodes;
using Factory.Core;
using Factory.Core.Abstractions;
using Factory.Data.Abstractions;
using Factory.Store.Abstractions;

namespace Factory.Store;

/// <summary>board.json and events.jsonl as exports of the database, with .export.json recording how far they got.</summary>
public sealed class StateExport(IFactoryPaths paths, IFileStore files, IBoardRows rows, IPause pause) : IStateExport
{
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(50);

    private string ManifestPath => Path.Combine(paths.Home, ".export.json");

    public void Export()
    {
        using var held = TakeLock();
        if (held is null) return;
        try { Write(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    // 100 polls 50 ms apart: export.lock is held only while another process writes the files.
    private IDisposable? TakeLock()
    {
        var path = Path.Combine(paths.Home, "export.lock");
        for (var i = 0; i < 100; i++)
        {
            if (files.TryLock(path) is { } held) return held;
            pause.Sleep(Poll);
        }
        return files.TryLock(path);
    }

    private void Write()
    {
        var (doneRevision, doneSeq) = Manifest();
        var revision = rows.Revision();
        if (revision == 0) return;
        if (doneRevision == revision && files.Stat(paths.BoardPath) is not null) return;
        if (rows.Read() is not { } read) return;

        var text = PsJson.Write(read.Board, Environment.NewLine);
        if (!string.Equals(files.ReadText(paths.BoardPath), text, StringComparison.Ordinal)) files.WriteAtomic(paths.BoardPath, Encoding.UTF8.GetBytes(text));

        // No manifest, or one ahead of the database (a new database): events.jsonl is rebuilt whole.
        var whole = doneRevision is null || revision < doneRevision;
        var events = rows.EventsAfter(whole ? 0 : doneSeq);
        var lines = string.Concat(events.Select(e => e.Line + "\n"));
        if (whole) files.WriteAtomic(paths.EventsPath, Encoding.UTF8.GetBytes(lines));
        else if (lines.Length > 0) files.AppendText(paths.EventsPath, lines);
        var seq = events.Count > 0 ? events[^1].Seq : whole ? 0 : doneSeq;
        files.WriteAtomic(ManifestPath, Encoding.UTF8.GetBytes(new JsonObject { ["revision"] = revision, ["seq"] = seq }.ToJsonString()));
    }

    private (long? Revision, long Seq) Manifest()
    {
        try
        {
            if (files.ReadText(ManifestPath) is { } t && JsonNode.Parse(t) is JsonObject m)
                return (m["revision"]?.GetValue<long>(), m["seq"]?.GetValue<long>() ?? 0);
        }
        catch (Exception e) when (e is System.Text.Json.JsonException or InvalidOperationException or FormatException) { }
        return (null, 0);
    }
}
