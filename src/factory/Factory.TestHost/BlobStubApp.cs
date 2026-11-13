using System.Globalization;
using System.Security;
using System.Text;
using System.Text.Json.Nodes;

namespace Factory.TestHost;

/// <summary>
/// A stand-in for the slice of the Azure Blob REST API the azure state backend uses (it was
/// factory/tests/fixtures/blob-stub.ps1). Blobs are kept in memory, keyed &lt;container&gt;/&lt;blob&gt;, with versioning on: an
/// overwrite or a delete keeps the old content as a version, and a version is deleted with ?versionid=. Every request needs
/// <c>Authorization: Bearer &lt;token&gt;</c>; anything else is 403 (the real account has shared keys off). Served, under
/// /&lt;container&gt;/&lt;blob&gt;: PUT (x-ms-blob-type BlockBlob | AppendBlob; If-Match, If-None-Match: *) · GET (If-None-Match) ·
/// DELETE [?versionid=] · PUT ?comp=appendblock · PUT ?comp=lease (acquire, renew, release, break) ·
/// GET ?restype=container&amp;comp=list[&amp;prefix=][&amp;include=versions][&amp;marker=] (pages of the page size). Leases run on the
/// clock, scaled for tests. A write to a leased blob without its lease id is 412. GET /_stub/state answers the blobs as JSON,
/// PUT /_stub/token changes the token it accepts, PUT /_stub/deny-version-delete (true or false) answers every version delete
/// as a principal without Storage Blob Data Owner gets it, and GET|PUT /_stub/ip is allow-ip's public-IP lookup. Every
/// request is appended to the log, when there is one, as { method, path, query }.
/// </summary>
public sealed class BlobStubApp(string token = "test-token", string? log = null, int pageSize = 1000, double leaseScale = 1.0, TimeProvider? clock = null)
{
    private static readonly UTF8Encoding Utf8 = new(false);
    private readonly Dictionary<string, StoredBlob> _blobs = [];
    private readonly List<BlobVersion> _versions = [];
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private string _token = token;
    private bool _denyVersionDelete;
    private string? _ip;
    private long _ticks;

    public async Task<StubReply> Handle(HttpContext ctx)
    {
        var body = await StubHost.BodyBytes(ctx);
        var req = ctx.Request;
        if (log is not null)
            File.AppendAllText(log, new JsonObject { ["method"] = req.Method, ["path"] = req.Path.Value, ["query"] = req.QueryString.Value ?? "" }.ToJsonString() + "\n");
        return Error(Route(req, body));
    }

    private StubReply Route(HttpRequest req, byte[] body)
    {
        var path = Uri.UnescapeDataString((req.Path.Value ?? "").TrimStart('/'));
        var q = req.Query;
        if (path == "_stub/state") return StubReply.Json(200, StateJson());
        if (path == "_stub/deny-version-delete" && req.Method == "PUT") { _denyVersionDelete = Utf8.GetString(body) == "true"; return new(200); }
        if (path == "_stub/token" && req.Method == "PUT") { _token = Utf8.GetString(body); return new(200); }
        // What allow-ip's "what is my IP" call answers (FACTORY_IP_URL); PUT changes it.
        if (path == "_stub/ip")
        {
            if (req.Method == "PUT") _ip = Utf8.GetString(body);
            return new(200, Text: _ip ?? "203.0.113.7");
        }
        if (req.Headers.Authorization.ToString() != $"Bearer {_token}") return Code(403, "AuthorizationPermissionMismatch");
        if (!path.Contains('/'))
            return req.Method == "GET" && q["comp"] == "list" ? List(path, q) : Code(400, "InvalidUri");
        // <container>/<blob>, so containers never share blobs.
        return req.Method switch
        {
            "PUT" when q["comp"] == "appendblock" => Append(path, req, body),
            "PUT" when q["comp"] == "lease" => Lease(path, req),
            "PUT" => Put(path, req, body),
            "GET" => Get(path, req),
            "DELETE" => Delete(path, req, q["versionid"].ToString()),
            _ => Code(405, "UnsupportedHttpVerb"),
        };
    }

    private StubReply Put(string name, HttpRequest req, byte[] body)
    {
        _blobs.TryGetValue(name, out var b);
        var ifMatch = req.Headers.IfMatch.ToString();
        if (req.Headers.IfNoneMatch.ToString() == "*" && b is not null) return Code(409, "BlobAlreadyExists");
        if (ifMatch.Length > 0 && (b is null || b.ETag != ifMatch)) return Code(412, "ConditionNotMet");
        if (LeaseRefusal(b, req) is { } refused) return refused;
        if (b is not null) Keep(name, b);
        var type = req.Headers["x-ms-blob-type"].ToString();
        if (type is not ("BlockBlob" or "AppendBlob")) return Code(400, "MissingRequiredHeader");
        var blob = new StoredBlob { Type = type, Bytes = type == "AppendBlob" ? [] : body, ETag = NewETag(), Modified = Now(), VersionId = NewVersionId(), Lease = b?.Lease };
        _blobs[name] = blob;
        return new(201, Headers: new Dictionary<string, string> { ["ETag"] = blob.ETag, ["x-ms-version-id"] = blob.VersionId });
    }

    private StubReply Append(string name, HttpRequest req, byte[] body)
    {
        if (!_blobs.TryGetValue(name, out var b)) return Code(404, "BlobNotFound");
        if (b.Type != "AppendBlob") return Code(409, "InvalidBlobType");
        if (LeaseRefusal(b, req) is { } refused) return refused;
        var offset = b.Bytes.Length;
        b.Bytes = [.. b.Bytes, .. body];
        b.ETag = NewETag();
        b.Modified = Now();
        return new(201, Headers: new Dictionary<string, string> { ["ETag"] = b.ETag, ["x-ms-blob-append-offset"] = $"{offset}" });
    }

    private StubReply Get(string name, HttpRequest req)
    {
        if (!_blobs.TryGetValue(name, out var b)) return Code(404, "BlobNotFound");
        if (req.Headers.IfNoneMatch.ToString() == b.ETag) return new(304, Headers: new Dictionary<string, string> { ["ETag"] = b.ETag });
        return new(200, Bytes: b.Bytes, ContentType: "application/octet-stream", Headers: new Dictionary<string, string> { ["ETag"] = b.ETag, ["x-ms-blob-type"] = b.Type });
    }

    private StubReply Delete(string name, HttpRequest req, string versionId)
    {
        if (versionId.Length > 0)
        {
            if (_denyVersionDelete) return Code(403, "OperationNotAllowedOnAutomaticSnapshot");
            if (_blobs.TryGetValue(name, out var cur) && cur.VersionId == versionId) return Code(403, "OperationNotAllowedOnRootBlob");
            var v = _versions.FirstOrDefault(x => x.Name == name && x.VersionId == versionId);
            if (v is null) return Code(404, "BlobNotFound");
            _versions.Remove(v);
            return new(202);
        }
        if (!_blobs.TryGetValue(name, out var b)) return Code(404, "BlobNotFound");
        if (LeaseRefusal(b, req) is { } refused) return refused;
        Keep(name, b);
        _blobs.Remove(name);
        return new(202);
    }

    private StubReply Lease(string name, HttpRequest req)
    {
        if (!_blobs.TryGetValue(name, out var b)) return Code(404, "BlobNotFound");
        var id = req.Headers["x-ms-lease-id"].ToString();
        switch (req.Headers["x-ms-lease-action"].ToString())
        {
            case "acquire":
                var dur = int.Parse(req.Headers["x-ms-lease-duration"].ToString(), CultureInfo.InvariantCulture);
                var want = req.Headers["x-ms-proposed-lease-id"].ToString() is { Length: > 0 } p ? p : Guid.NewGuid().ToString();
                if (Active(b) && b.Lease!.Id != want) return Code(409, "LeaseAlreadyPresent");
                b.Lease = new BlobLease { Id = want, Until = Until(dur), Duration = dur };
                return new(201, Headers: new Dictionary<string, string> { ["x-ms-lease-id"] = want });
            case "renew":
                // A lapsed lease can be renewed while nobody else has taken it, as on the real service.
                if (b.Lease is null || b.Lease.Id != id) return Code(409, "LeaseIdMismatchWithLeaseOperation");
                b.Lease.Until = Until(b.Lease.Duration);
                return new(200, Headers: new Dictionary<string, string> { ["x-ms-lease-id"] = id });
            case "release":
                if (b.Lease is null || b.Lease.Id != id) return Code(409, "LeaseIdMismatchWithLeaseOperation");
                b.Lease = null;
                return new(200);
            case "break":
                b.Lease = null;
                return new(202);
            default:
                return Code(400, "InvalidHeaderValue");
        }
    }

    private StubReply List(string container, IQueryCollection q)
    {
        var prefix = $"{container}/{q["prefix"]}";
        var withVersions = q["include"].ToString().Contains("versions", StringComparison.Ordinal);
        var rows = _blobs.Where(kv => kv.Key.StartsWith(prefix, StringComparison.Ordinal))
            .Select(kv => (Name: kv.Key, kv.Value.VersionId, Current: true, Blob: (StoredBlob?)kv.Value, kv.Value.Bytes, kv.Value.Modified, kv.Value.ETag, kv.Value.Type)).ToList();
        if (withVersions)
            rows.AddRange(_versions.Where(v => v.Name.StartsWith(prefix, StringComparison.Ordinal))
                .Select(v => (v.Name, v.VersionId, Current: false, Blob: (StoredBlob?)null, v.Bytes, v.Modified, ETag: "\"0xV\"", v.Type)));
        var sorted = rows.OrderBy(r => r.Name, StringComparer.Ordinal).ThenBy(r => r.VersionId, StringComparer.Ordinal).ToList();
        var start = int.TryParse(q["marker"], out var mk) ? mk : 0;
        var next = start + pageSize < sorted.Count ? $"{start + pageSize}" : "";
        var xml = new StringBuilder("<?xml version=\"1.0\" encoding=\"utf-8\"?><EnumerationResults><Blobs>");
        foreach (var r in sorted.Skip(start).Take(pageSize))
        {
            xml.Append($"<Blob><Name>{SecurityElement.Escape(r.Name[(container.Length + 1)..])}</Name>");
            if (withVersions) xml.Append($"<VersionId>{r.VersionId}</VersionId>");
            if (r.Current) xml.Append("<IsCurrentVersion>true</IsCurrentVersion>");
            var lease = r.Current && Active(r.Blob) ? "leased" : "available";
            xml.Append($"<Properties><Last-Modified>{r.Modified.ToString("R", CultureInfo.InvariantCulture)}</Last-Modified><Etag>{r.ETag}</Etag>");
            xml.Append($"<Content-Length>{r.Bytes.Length}</Content-Length><BlobType>{r.Type}</BlobType><LeaseState>{lease}</LeaseState></Properties></Blob>");
        }
        xml.Append($"</Blobs><NextMarker>{next}</NextMarker></EnumerationResults>");
        return new(200, Text: xml.ToString(), ContentType: "application/xml");
    }

    private JsonObject StateJson()
    {
        var blobs = new JsonObject();
        foreach (var (n, b) in _blobs) blobs[n] = new JsonObject { ["type"] = b.Type, ["text"] = Utf8.GetString(b.Bytes), ["leased"] = Active(b) };
        return new JsonObject { ["blobs"] = blobs, ["versions"] = new JsonArray([.. _versions.Select(v => (JsonNode)new JsonObject { ["name"] = v.Name, ["versionId"] = v.VersionId })]) };
    }

    private bool Active(StoredBlob? b) => b?.Lease is { } l && (l.Until == DateTime.MaxValue || l.Until > Now());

    /// <summary>412 when the blob is leased and the request does not carry that lease, else null.</summary>
    private StubReply? LeaseRefusal(StoredBlob? b, HttpRequest req) =>
        Active(b) && req.Headers["x-ms-lease-id"].ToString() != b!.Lease!.Id ? Code(412, "LeaseIdMissing") : null;

    private DateTime Until(int duration) => duration == -1 ? DateTime.MaxValue : Now().AddSeconds(duration * leaseScale);

    private void Keep(string name, StoredBlob b) => _versions.Add(new BlobVersion(name, b.VersionId, b.Bytes, b.Type, b.Modified));

    private DateTime Now() => _clock.GetUtcNow().UtcDateTime;

    private string NewVersionId() => Now().AddTicks(++_ticks).ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture);

    private static string NewETag() => "\"0x" + Guid.NewGuid().ToString("N")[..16].ToUpperInvariant() + "\"";

    private static StubReply Code(int status, string code) => new(status, Text: code, Headers: new Dictionary<string, string> { ["x-ms-error-code"] = code });

    /// <summary>An error code goes out as the header and as Azure's XML body.</summary>
    private static StubReply Error(StubReply r) =>
        r.Headers is { } h && h.TryGetValue("x-ms-error-code", out var code) && r.Text == code
            ? r with { Text = $"<?xml version=\"1.0\"?><Error><Code>{code}</Code></Error>", ContentType = "application/xml" }
            : r;
}
