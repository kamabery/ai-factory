using Factory.Store.Abstractions;

namespace Factory.Store;

/// <summary>deploy.ps1's <c>Invoke-WebRequest -SkipHttpErrorCheck</c> over <see cref="HttpClient"/>, with no credentials.</summary>
public sealed class HttpProbe(HttpClient http) : IHttpProbe
{
    public ProbeResult Get(string url, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            using var res = http.Send(new HttpRequestMessage(HttpMethod.Get, url), cts.Token);
            var body = res.Content.ReadAsStringAsync(cts.Token).GetAwaiter().GetResult();
            var server = res.Headers.TryGetValues("Server", out var s) ? string.Join(' ', s) : "-";
            return new ProbeResult((int)res.StatusCode, body, server.Length > 0 ? server : "-");
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or OperationCanceledException or InvalidOperationException or UriFormatException)
        {
            return new ProbeResult(0, e.Message, "-");
        }
    }
}
