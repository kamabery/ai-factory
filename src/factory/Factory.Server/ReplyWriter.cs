using System.Text;

namespace Factory.Server;

/// <summary>Writes a <see cref="Reply"/>: always <c>no-store</c>, always <c>charset=utf-8</c>, as serve.ps1 did.</summary>
public static class ReplyWriter
{
    private static readonly Dictionary<string, string> Types = new(StringComparer.OrdinalIgnoreCase)
    {
        [".html"] = "text/html", [".json"] = "application/json", [".jsonl"] = "text/plain", [".md"] = "text/plain",
        [".log"] = "text/plain", [".txt"] = "text/plain", [".js"] = "text/javascript", [".css"] = "text/css",
    };

    public static async Task WriteAsync(HttpContext ctx, Reply reply)
    {
        if (reply.FilePath is { } path)
        {
            var bytes = ReadShared(path);
            if (bytes is null) { await WriteAsync(ctx, Reply.NotFound()); return; }
            var type = Types.GetValueOrDefault(Path.GetExtension(path), "application/octet-stream");
            await Send(ctx, 200, bytes, type);
            return;
        }
        await Send(ctx, reply.Status, Encoding.UTF8.GetBytes(reply.Body), reply.Type);
    }

    private static async Task Send(HttpContext ctx, int status, byte[] bytes, string type)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = $"{type}; charset=utf-8";
        ctx.Response.Headers.CacheControl = "no-store";
        ctx.Response.ContentLength = bytes.Length;
        await ctx.Response.Body.WriteAsync(bytes);
    }

    /// <summary>Not File.ReadAllBytes: its FileShare.Read would block the runner appending to a live run log.</summary>
    internal static byte[]? ReadShared(string path)
    {
        if (!System.IO.File.Exists(path)) return null;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var ms = new MemoryStream();
            fs.CopyTo(ms);
            return ms.ToArray();
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }
}
