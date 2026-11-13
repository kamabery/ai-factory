using System.Text.Json.Nodes;
using Factory.Core;
using Factory.Store.Abstractions;
using NSubstitute;

namespace Factory.UnitTests;

/// <summary>
/// GitHub's REST API, as far as a preview deploy uses it, behind an <see cref="IGitHub"/> substitute: the preview
/// environment and variables, the OIDC subject, workflow runs and their jobs' logs. Each request is recorded; set
/// <see cref="Override"/> to answer one differently.
/// </summary>
public sealed class FakeActions
{
    public IGitHub GitHub { get; } = Substitute.For<IGitHub>();
    public List<(string Method, string Path, JsonNode? Body)> Requests { get; } = [];
    public List<JsonObject> Runs { get; } = [];

    /// <summary>The conclusion a dispatched run reaches.</summary>
    public string Conclusion { get; set; } = "success";

    /// <summary>How many looks a dispatched run stays in_progress for.</summary>
    public int Looks { get; set; }

    /// <summary>The OIDC customization GitHub answers.</summary>
    public JsonNode Oidc { get; set; } = new JsonObject { ["use_default"] = true };

    /// <summary>Answers a request before the default world does, or returns null to let it.</summary>
    public Func<string, string, GitHubResponse?>? Override { get; set; }

    private readonly Dictionary<long, int> _looks = [];

    public FakeActions()
    {
        GitHub.Send(Arg.Any<HttpMethod>(), Arg.Any<string>(), Arg.Any<JsonNode?>())
            .Returns(c => Answer(c.ArgAt<HttpMethod>(0).Method, c.ArgAt<string>(1), c.ArgAt<JsonNode?>(2)));
        GitHub.GitAuth().Returns(["-c", "auth"]);
        GitHub.CloneUrl(Arg.Any<string>()).Returns(c => $"https://git/{c.Arg<string>()}.git");
    }

    public JsonObject AddRun(long id, string sha, string status, string? conclusion = null, string branch = "factory/J-001") =>
        Add(new JsonObject
        {
            ["id"] = id, ["head_sha"] = sha, ["head_branch"] = branch, ["status"] = status, ["conclusion"] = conclusion,
            ["html_url"] = $"https://gh/runs/{id}",
        });

    private JsonObject Add(JsonObject run) { Runs.Add(run); return run; }

    public IEnumerable<(string Method, string Path, JsonNode? Body)> To(string fragment) => Requests.Where(r => r.Path.Contains(fragment, StringComparison.Ordinal));

    private GitHubResponse Answer(string method, string path, JsonNode? body)
    {
        Requests.Add((method, path, body?.DeepClone()));
        if (Override?.Invoke(method, path) is { } custom) return custom;
        if (method == "PUT" && path.EndsWith("/environments/preview", StringComparison.Ordinal)) return new(200, new JsonObject(), null);
        if (method == "POST" && path.EndsWith("/variables", StringComparison.Ordinal)) return new(201, new JsonObject(), null);
        if (method == "PATCH" && path.Contains("/variables/", StringComparison.Ordinal)) return new(204, null, null);
        if (path.EndsWith("/actions/oidc/customization/sub", StringComparison.Ordinal)) return new(200, Oidc.DeepClone(), null);
        if (method == "POST" && path.EndsWith("/dispatches", StringComparison.Ordinal))
        {
            var id = Runs.Count == 0 ? 100 : Runs.Max(r => (long)r["id"]!) + 1;
            var run = AddRun(id, "dispatched", Looks > 0 ? "in_progress" : "completed", Looks > 0 ? null : Conclusion, (string)body!["ref"]!);
            run["inputs"] = body["inputs"]!.DeepClone();
            _looks[id] = Looks;
            return new(204, null, null);
        }
        if (path.Contains("/actions/runs?", StringComparison.Ordinal))
            return new(200, new JsonObject { ["workflow_runs"] = new JsonArray([.. Runs.Select(r => r.DeepClone())]) }, null);
        if (path.EndsWith("/jobs", StringComparison.Ordinal))
            return new(200, new JsonObject { ["jobs"] = new JsonArray(new JsonObject { ["id"] = 7, ["name"] = "deploy", ["conclusion"] = "failure" }, new JsonObject { ["id"] = 8, ["name"] = "build", ["conclusion"] = "success" }) }, null);
        if (path.EndsWith("/logs", StringComparison.Ordinal)) return new(200, JsonValue.Create("Error: boom"), null);
        if (path.Contains("/actions/runs/", StringComparison.Ordinal))
        {
            var id = long.Parse(path[(path.LastIndexOf('/') + 1)..], System.Globalization.CultureInfo.InvariantCulture);
            var run = Runs.Single(r => (long)r["id"]! == id);
            if (_looks.TryGetValue(id, out var left) && left > 0 && --_looks[id] == 0) { run["status"] = "completed"; run["conclusion"] = Conclusion; }
            return new(200, run.DeepClone(), null);
        }
        return new(404, new JsonObject { ["message"] = "Not Found" }, null);
    }
}
