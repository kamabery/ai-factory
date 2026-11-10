using System.Text;
using Factory.Store;

namespace Factory.IntegrationTests;

public class FileStoreTests
{
    private readonly FileStore _store = new();

    [Fact]
    public void Reads_a_file_and_returns_null_for_a_missing_one()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir["a.json"], "{\"x\":\"é\"}");

        Assert.Equal("{\"x\":\"é\"}", _store.ReadText(dir["a.json"]));
        Assert.Null(_store.ReadText(dir["missing.json"]));
        Assert.Null(_store.ReadBytes(dir["missing.json"]));
        Assert.Null(_store.Stat(dir["missing.json"]));
    }

    [Fact]
    public void Ensures_a_nested_directory_and_is_idempotent()
    {
        using var dir = new TempDir();

        _store.EnsureDirectory(dir["a/b"]);
        _store.EnsureDirectory(dir["a/b"]);

        Assert.True(Directory.Exists(dir["a/b"]));
    }

    [Fact]
    public void Writes_atomically_creating_the_folder_and_leaving_no_temp_file()
    {
        using var dir = new TempDir();

        _store.WriteAtomic(dir["x/board.json"], "{}"u8.ToArray());
        _store.WriteAtomic(dir["x/board.json"], "{1}"u8.ToArray());

        Assert.Equal("{1}", File.ReadAllText(dir["x/board.json"]));
        Assert.Equal(["board.json"], Directory.GetFiles(dir["x"]).Select(Path.GetFileName));
    }

    [Fact]
    public async Task Retries_the_rename_while_a_reader_holds_the_file_without_delete_sharing()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir["board.json"], "old");
        var reader = new FileStream(dir["board.json"], FileMode.Open, FileAccess.Read, FileShare.Read);
        var ct = TestContext.Current.CancellationToken;
        var released = Task.Run(async () => { await Task.Delay(300, ct); reader.Dispose(); }, ct);

        _store.WriteAtomic(dir["board.json"], "new"u8.ToArray());

        await released;
        Assert.Equal("new", File.ReadAllText(dir["board.json"]));
    }

    [Fact]
    public void Reads_bytes_while_a_writer_holds_the_file()
    {
        using var dir = new TempDir();
        using var writer = new FileStream(dir["run.jsonl"], FileMode.Create, FileAccess.Write, FileShare.Read);
        writer.Write("line\n"u8);
        writer.Flush();

        Assert.Equal("line\n", Encoding.UTF8.GetString(_store.ReadBytes(dir["run.jsonl"])!));
    }

    [Fact]
    public void Appends_utf8_without_a_byte_order_mark()
    {
        using var dir = new TempDir();

        _store.AppendText(dir["events.jsonl"], "é\n");
        _store.AppendText(dir["events.jsonl"], "b\n");

        Assert.Equal("é\nb\n"u8.ToArray(), File.ReadAllBytes(dir["events.jsonl"]));
    }

    [Fact]
    public void Lists_every_file_hidden_and_nested_with_its_length_and_write_time()
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(dir["h/.local"]);
        File.WriteAllText(dir["h/.local/t.json"], "12");
        File.WriteAllText(dir["h/.sync.json"], "{}");
        File.SetAttributes(dir["h/.sync.json"], FileAttributes.Hidden);

        var files = _store.Files(dir["h"]).OrderBy(f => f.Path, StringComparer.Ordinal).ToList();

        Assert.Equal([Path.GetFullPath(dir["h/.local/t.json"]), Path.GetFullPath(dir["h/.sync.json"])], files.Select(f => f.Path));
        Assert.Equal(2, files[0].Length);
        Assert.Equal(File.GetLastWriteTimeUtc(dir["h/.local/t.json"]).Ticks, files[0].LastWriteTicks);
        Assert.Empty(_store.Files(dir["missing"]));
    }
}
