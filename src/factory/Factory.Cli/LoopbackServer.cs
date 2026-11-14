using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Factory.Core;

namespace Factory.Cli;

/// <summary>
/// init's browser callbacks (contract, "Secrets, init and doctor"): an HTTP/1.1 listener on 127.0.0.1 and a free port, with
/// no dependency and no URL reservation. It serves the pages it is given and waits for a GET on a callback path.
/// </summary>
public sealed class LoopbackServer : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly ConcurrentDictionary<string, string> _pages = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, BlockingCollection<IReadOnlyDictionary<string, string>>> _callbacks = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _stop = new();

    public LoopbackServer()
    {
        _listener.Start();
        _ = Task.Run(Accept);
    }

    /// <summary>http://127.0.0.1:&lt;port&gt;</summary>
    public string Base => $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";

    public void Page(string path, string html) => _pages[path] = html;

    /// <summary>Waits for a GET on <paramref name="path"/> and returns its query; refuses after <paramref name="timeout"/>.</summary>
    public IReadOnlyDictionary<string, string> Wait(string path, TimeSpan timeout)
    {
        var queue = _callbacks.GetOrAdd(path, _ => []);
        return queue.TryTake(out var query, timeout) ? query
            : throw new FactoryException($"init: nothing came back to {Base}{path} within {timeout.TotalMinutes:0} minutes; run factory init again");
    }

    private async Task Accept()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_stop.Token); }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException) { return; }
            _ = Task.Run(() => Serve(client));
        }
    }

    private void Serve(TcpClient client)
    {
        using (client)
        using (var stream = client.GetStream())
        {
            var reader = new StreamReader(stream, Encoding.ASCII, false, 4096, leaveOpen: true);
            var request = reader.ReadLine() ?? "";
            while (!string.IsNullOrEmpty(reader.ReadLine())) { }   // the headers
            var target = request.Split(' ').ElementAtOrDefault(1) ?? "/";
            var (path, query) = Split(target);
            string body;
            int status = 200;
            if (_pages.TryGetValue(path, out var page)) body = page;
            else if (_callbacks.GetOrAdd(path, _ => []) is { } q && Waited(path))
            {
                q.Add(query);
                body = "<!doctype html><title>AI factory</title><p>Done. You can close this tab and go back to the terminal.</p>";
            }
            else { status = 404; body = "not found"; }
            var bytes = Encoding.UTF8.GetBytes(body);
            var head = Encoding.ASCII.GetBytes($"HTTP/1.1 {status} {(status == 200 ? "OK" : "Not Found")}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n");
            stream.Write(head);
            stream.Write(bytes);
        }
    }

    // A callback path is one init waits on: /callback (the App's creation) and /oauth (the sign-in).
    private static bool Waited(string path) => path is "/callback" or "/oauth";

    private static (string Path, IReadOnlyDictionary<string, string> Query) Split(string target)
    {
        var i = target.IndexOf('?', StringComparison.Ordinal);
        var path = i < 0 ? target : target[..i];
        var query = new Dictionary<string, string>(StringComparer.Ordinal);
        if (i >= 0)
            foreach (var pair in target[(i + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = pair.Split('=', 2);
                query[Uri.UnescapeDataString(kv[0])] = kv.Length > 1 ? Uri.UnescapeDataString(kv[1].Replace('+', ' ')) : "";
            }
        return (path, query);
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
        _stop.Dispose();
    }
}
