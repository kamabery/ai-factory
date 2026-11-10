namespace Factory.Store.Abstractions;

/// <summary>The slice of the Blob REST API the azure backend uses. Failures throw factory.ps1's "state: ..." messages.</summary>
public interface IBlobClient
{
    /// <summary>Null when there is no such blob; <see cref="BlobRead.NotModified"/> when <paramref name="etag"/> is current.</summary>
    BlobRead? Read(string name, string? etag = null);

    /// <summary>Writes a block blob and returns its ETag. A failed condition is a "state conflict".</summary>
    string Write(string name, byte[] bytes, string? ifMatch = null, bool ifNoneMatchAny = false);

    /// <summary>Appends one block to an append blob, creating the blob first if need be.</summary>
    AppendResult Append(string name, byte[] bytes);

    /// <summary>Deletes a blob, or one of its old versions; a missing one is fine.</summary>
    void Delete(string name, string? versionId = null);

    /// <summary>Every blob under <paramref name="prefix"/>, and with <paramref name="versions"/> every old version too.</summary>
    IReadOnlyList<BlobItem> List(string prefix = "", bool versions = false);

    /// <summary>Takes the 60 s lease on <c>locks/{name}</c>, waiting up to <paramref name="timeoutSeconds"/>; null when it could not.</summary>
    string? AcquireLease(string name, int timeoutSeconds);

    /// <summary>True when the lease is still ours (a lapsed one nobody took counts), false when it is gone.</summary>
    bool RenewLease(string name, string id);

    void ReleaseLease(string name, string id);
}
