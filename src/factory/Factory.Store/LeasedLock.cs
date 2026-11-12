using System.Diagnostics;
using System.Globalization;
using System.Text;
using Factory.Store.Abstractions;
using Microsoft.Extensions.Options;

namespace Factory.Store;

/// <summary>
/// A lock file plus, in azure, its lease: the file is created new with this process's PID (a watchdog or a busy check
/// reads it), and a file whose PID is no longer running is taken over. In azure the lease on <c>locks/&lt;lease&gt;</c>
/// is taken without waiting and renewed every FACTORY_STATE_RENEW_SECONDS (20) until it is lost. runner.lock
/// (<see cref="RunnerLock"/>) and an idea session's turn.lock are both one.
/// </summary>
public sealed class LeasedLock(string lockPath, string lease, IStateSettings settings, IBlobClient blobs, IOptions<StateOptions> options)
{
    private string LockPath => lockPath;

    private IBlobClient Blobs => blobs;

    private string Lease => lease;

    public IDisposable? TryTake()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(LockPath))!);
        if (!TakeFile()) return null;
        if (!settings.IsAzure) return new Held(this, null, null);
        var id = blobs.AcquireLease(lease, 0);
        if (id is null) { DeleteFile(); return null; }
        var every = int.TryParse(options.Value.RenewSeconds, NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) ? s : 20;
        Timer? keeper = null;
        keeper = new Timer(_ =>
        {
            try { if (!blobs.RenewLease(lease, id)) keeper?.Dispose(); }
            catch (Exception e) when (e is Core.FactoryException or HttpRequestException) { }
        }, null, TimeSpan.FromSeconds(every), TimeSpan.FromSeconds(every));
        return new Held(this, id, keeper);
    }

    private bool TakeFile()
    {
        if (CreateNew()) return true;
        var holder = int.TryParse(ReadHolder(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid) ? pid : 0;
        if (holder > 0 && Alive(holder)) return false;
        DeleteFile();
        return CreateNew();
    }

    private bool CreateNew()
    {
        try
        {
            using var fs = new FileStream(LockPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            fs.Write(Encoding.ASCII.GetBytes(Environment.ProcessId.ToString(CultureInfo.InvariantCulture)));
            return true;
        }
        catch (IOException) { return false; }
    }

    private string? ReadHolder()
    {
        try { return File.ReadLines(LockPath).FirstOrDefault()?.Trim(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    private static bool Alive(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return !p.HasExited; }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException) { return false; }
    }

    private void DeleteFile()
    {
        try { File.Delete(LockPath); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private sealed class Held(LeasedLock owner, string? id, Timer? keeper) : IDisposable
    {
        private bool _done;

        public void Dispose()
        {
            if (_done) return;
            _done = true;
            keeper?.Dispose();
            if (id is not null) try { owner.Blobs.ReleaseLease(owner.Lease, id); } catch (Core.FactoryException) { }
            owner.DeleteFile();
        }
    }
}
