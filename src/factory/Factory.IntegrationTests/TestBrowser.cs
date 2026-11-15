using System.Net;
using System.Text.RegularExpressions;
using Factory.Cli.Abstractions;

namespace Factory.IntegrationTests;

/// <summary>
/// A human at the browser for init's tests: the start page's form is posted as a browser would, and every other URL is
/// fetched with its redirects followed (to the GitHub stub, then back to init's loopback callback).
/// </summary>
public sealed partial class TestBrowser : IBrowser
{
    private readonly HttpClient _http = new(new HttpClientHandler { AllowAutoRedirect = true });

    public List<string> Opened { get; } = [];

    /// <summary>Posts the manifest with another state, as a forged callback would carry.</summary>
    public bool ForgeState { get; set; }

    public void Open(string url)
    {
        Opened.Add(url);
        // A test never reaches the real GitHub: a URL off this machine means init read the wrong settings.
        if (new Uri(url).Host is not ("localhost" or "127.0.0.1")) throw new InvalidOperationException($"the test browser was sent off this machine: {url}");
        if (!url.EndsWith("/start", StringComparison.Ordinal))
        {
            using var got = _http.GetAsync(url).Result;
            if (!got.IsSuccessStatusCode) throw new InvalidOperationException($"the browser got {(int)got.StatusCode} from {got.RequestMessage?.RequestUri}: {got.Content.ReadAsStringAsync().Result}");
            return;
        }
        var page = _http.GetStringAsync(url).Result;
        var action = WebUtility.HtmlDecode(Action().Match(page).Groups[1].Value);
        var manifest = WebUtility.HtmlDecode(Manifest().Match(page).Groups[1].Value);
        if (ForgeState) action = StateParam().Replace(action, "state=forged");
        if (new Uri(action).Host is not ("localhost" or "127.0.0.1")) throw new InvalidOperationException($"the start page posts off this machine: {action}");
        using var res = _http.PostAsync(action, new FormUrlEncodedContent(new Dictionary<string, string> { ["manifest"] = manifest })).Result;
        if (!res.IsSuccessStatusCode) throw new InvalidOperationException($"the browser got {(int)res.StatusCode} from {res.RequestMessage?.RequestUri} (posted to {action}): {res.Content.ReadAsStringAsync().Result}");
    }

    [GeneratedRegex(@"action=""([^""]+)""")] private static partial Regex Action();
    [GeneratedRegex(@"name=""manifest"" value=""([^""]*)""")] private static partial Regex Manifest();
    [GeneratedRegex(@"state=[0-9a-f]+")] private static partial Regex StateParam();
}
