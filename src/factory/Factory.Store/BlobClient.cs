using System.Globalization;
using System.Text;
using System.Xml.Linq;
using Factory.Core;
using Factory.Store.Abstractions;

namespace Factory.Store;

/// <summary>
/// state.ps1's Blob REST calls over <see cref="HttpClient"/>, with an Entra bearer token and no shared keys. Not the
/// Azure SDK: it refuses a bearer token over plain http, so it could not talk to blob-stub.ps1.
/// </summary>
public sealed class BlobClient(HttpClient http, IStateSettings settings, IStateToken token, TimeProvider clock) : IBlobClient
{
    public const int LeaseSeconds = 60;

    public BlobRead? Read(string name, string? etag = null)
    {
        var r = Send(HttpMethod.Get, name, headers: etag is null ? [] : [("If-None-Match", ETags.Quote(etag)!)]);
        return r.Status switch
        {
            404 => null,
            304 => new BlobRead(true, ETags.Bare(etag), null),
            200 => new BlobRead(false, ETags.Bare(r.Header("ETag")), r.Bytes),
            _ => throw Error(r, $"GET {name}"),
        };
    }

    public string Write(string name, byte[] bytes, string? ifMatch = null, bool ifNoneMatchAny = false)
    {
        var h = new List<(string, string)> { ("x-ms-blob-type", "BlockBlob") };
        if (ifMatch is not null) h.Add(("If-Match", ETags.Quote(ifMatch)!));
        if (ifNoneMatchAny) h.Add(("If-None-Match", "*"));
        var r = Send(HttpMethod.Put, name, headers: [.. h], body: bytes);
        if (r.Status == 412 || (r.Status == 409 && ifNoneMatchAny)) throw new FactoryException($"state conflict: {name} changed in Azure since it was read");
        if (r.Status != 201) throw Error(r, $"PUT {name}");
        return ETags.Bare(r.Header("ETag"))!;
    }

    public AppendResult Append(string name, byte[] bytes)
    {
        BlobResponse r = default!;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            r = Send(HttpMethod.Put, name, "comp=appendblock", body: bytes);
            if (r.Status == 201) return new AppendResult(ETags.Bare(r.Header("ETag"))!, long.Parse(r.Header("x-ms-blob-append-offset") ?? "0", CultureInfo.InvariantCulture));
            if (r.Status != 404) break;
            var c = Send(HttpMethod.Put, name, headers: [("x-ms-blob-type", "AppendBlob"), ("If-None-Match", "*")]);
            if (c.Status is not (201 or 409)) throw Error(c, $"create {name}");
        }
        throw Error(r, $"append to {name}");
    }

    public void Delete(string name, string? versionId = null)
    {
        var r = Send(HttpMethod.Delete, name, versionId is null ? "" : $"versionid={Uri.EscapeDataString(versionId)}");
        if (r.Status is not (202 or 404)) throw Error(r, $"DELETE {name}");
    }

    public IReadOnlyList<BlobItem> List(string prefix = "", bool versions = false)
    {
        var items = new List<BlobItem>();
        var marker = "";
        do
        {
            var q = $"restype=container&comp=list&prefix={Uri.EscapeDataString(prefix)}";
            if (versions) q += "&include=versions";
            if (marker.Length > 0) q += $"&marker={Uri.EscapeDataString(marker)}";
            var r = Send(HttpMethod.Get, "", q);
            if (r.Status != 200) throw Error(r, "list");
            var xml = XDocument.Parse(Encoding.UTF8.GetString(r.Bytes).TrimStart('﻿'));
            foreach (var b in xml.Root!.Element("Blobs")?.Elements("Blob") ?? [])
            {
                var p = b.Element("Properties")!;
                items.Add(new BlobItem(
                    b.Element("Name")!.Value,
                    b.Element("VersionId")?.Value,
                    !versions || b.Element("IsCurrentVersion")?.Value == "true",
                    long.Parse(p.Element("Content-Length")!.Value, CultureInfo.InvariantCulture),
                    DateTime.Parse(p.Element("Last-Modified")!.Value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal),
                    ETags.Bare(p.Element("Etag")?.Value),
                    p.Element("LeaseState")?.Value == "leased"));
            }
            marker = xml.Root.Element("NextMarker")?.Value ?? "";
        } while (marker.Length > 0);
        return items;
    }

    public string? AcquireLease(string name, int timeoutSeconds)
    {
        var blob = $"locks/{name}";
        var id = Guid.NewGuid().ToString();
        var deadline = clock.GetUtcNow().AddSeconds(timeoutSeconds);
        while (true)
        {
            var r = Send(HttpMethod.Put, blob, "comp=lease", [("x-ms-lease-action", "acquire"), ("x-ms-lease-duration", $"{LeaseSeconds}"), ("x-ms-proposed-lease-id", id)]);
            if (r.Status == 201) return id;
            if (r.Status == 404)
            {
                var c = Send(HttpMethod.Put, blob, headers: [("x-ms-blob-type", "BlockBlob"), ("If-None-Match", "*")]);
                if (c.Status is not (201 or 409)) throw Error(c, $"create {blob}");
                continue;
            }
            if (r.Status != 409) throw Error(r, $"lease {blob}");
            if (clock.GetUtcNow() >= deadline) return null;
            Thread.Sleep(500);
        }
    }

    public bool RenewLease(string name, string id)
    {
        var r = Send(HttpMethod.Put, $"locks/{name}", "comp=lease", [("x-ms-lease-action", "renew"), ("x-ms-lease-id", id)]);
        if (r.Status == 200) return true;
        if (r.Status == 409) return false;
        throw Error(r, $"renew locks/{name}");
    }

    public void ReleaseLease(string name, string id)
    {
        var r = Send(HttpMethod.Put, $"locks/{name}", "comp=lease", [("x-ms-lease-action", "release"), ("x-ms-lease-id", id)]);
        if (r.Status is not (200 or 409)) throw Error(r, $"release locks/{name}");
    }

    private static FactoryException Error(BlobResponse r, string what)
    {
        var code = r.Header("x-ms-error-code");
        var m = $"state: {what} answered {r.Status} {code}";
        // Deleting an old version needs deleteBlobVersion/action, which only Storage Blob Data Owner has.
        if (code == "OperationNotAllowedOnAutomaticSnapshot")
            m += " (deleting an old version needs Storage Blob Data Owner on the container, not Contributor: apply factory/infra/state)";
        return new FactoryException(m);
    }

    /// <summary>One Blob service call, retried on a transport error or a 5xx (three tries).</summary>
    private BlobResponse Send(HttpMethod method, string name, string query = "", (string Name, string Value)[]? headers = null, byte[]? body = null)
    {
        var uri = settings.ContainerUrl;
        if (name.Length > 0) uri += "/" + string.Join('/', name.Split('/').Select(Uri.EscapeDataString));
        if (query.Length > 0) uri += "?" + query;
        for (var attempt = 1; ; attempt++)
        {
            using var req = new HttpRequestMessage(method, uri);
            req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token.Get()}");
            req.Headers.TryAddWithoutValidation("x-ms-version", "2023-11-03");
            req.Headers.TryAddWithoutValidation("x-ms-date", clock.GetUtcNow().ToString("R", CultureInfo.InvariantCulture));
            foreach (var (k, v) in headers ?? []) req.Headers.TryAddWithoutValidation(k, v);
            if (body is not null || method == HttpMethod.Put) req.Content = new ByteArrayContent(body ?? []);
            HttpResponseMessage res;
            try { res = http.Send(req); }
            catch (HttpRequestException e)
            {
                if (attempt < 3) { Thread.Sleep(250 * attempt); continue; }
                throw new FactoryException($"state: {method} {name} failed: {e.Message}");
            }
            using (res)
            {
                var status = (int)res.StatusCode;
                if (status >= 500 && attempt < 3) { Thread.Sleep(250 * attempt); continue; }
                var h = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var kv in res.Headers.Concat(res.Content.Headers)) h[kv.Key] = string.Join(',', kv.Value);
                using var ms = new MemoryStream();
                res.Content.ReadAsStream().CopyTo(ms);
                return new BlobResponse(status, h, ms.ToArray());
            }
        }
    }
}
