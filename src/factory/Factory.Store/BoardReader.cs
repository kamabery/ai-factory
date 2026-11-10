using System.Text.Json.Nodes;
using Factory.Core;
using Factory.Core.Abstractions;
using Factory.Store.Abstractions;

namespace Factory.Store;

/// <summary>
/// Reads board.json from the local state folder (the file backend; the blob backend is spec 03). No lock:
/// factory.ps1 replaces board.json by rename, so a reader sees the old board or the new one, never half.
/// </summary>
public sealed class BoardReader(IFactoryPaths paths, IFileStore files) : IBoardReader
{
    public JsonObject Load()
    {
        files.EnsureDirectory(paths.Home); // factory.ps1 creates FACTORY_HOME on every command
        var text = files.ReadText(paths.BoardPath);
        return text is null ? Board.Empty() : Board.Parse(text);
    }
}
