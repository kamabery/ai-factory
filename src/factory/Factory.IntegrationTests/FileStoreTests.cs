using Factory.Store;

namespace Factory.IntegrationTests;

public class FileStoreTests
{
    [Fact]
    public void Reads_a_file_and_returns_null_for_a_missing_one()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir["a.json"], "{\"x\":\"é\"}");
        var store = new FileStore();

        Assert.Equal("{\"x\":\"é\"}", store.ReadText(dir["a.json"]));
        Assert.Null(store.ReadText(dir["missing.json"]));
    }

    [Fact]
    public void Ensures_a_nested_directory_and_is_idempotent()
    {
        using var dir = new TempDir();
        var store = new FileStore();

        store.EnsureDirectory(dir["a/b"]);
        store.EnsureDirectory(dir["a/b"]);

        Assert.True(Directory.Exists(dir["a/b"]));
    }
}
