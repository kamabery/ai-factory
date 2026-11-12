using System.Diagnostics;
using System.Globalization;
using System.Text;
using Factory.Store.Abstractions;
using Microsoft.Extensions.Options;

namespace Factory.Store;

/// <summary>
/// runner.ps1's Take-Lock and its azure lease: <c>runner.lock</c> is created new with this process's PID (serve.ps1's
/// watchdog reads it), and a lock whose PID is no longer running is taken over. In azure the lease on
/// <c>locks/runner</c> is taken without waiting and renewed every FACTORY_STATE_RENEW_SECONDS (20) until it is lost.
/// </summary>
public sealed class RunnerLock(IFactoryPaths paths, IStateSettings settings, IBlobClient blobs, IOptions<StateOptions> options) : IRunnerLock
{
    private string LockPath => Path.Combine(paths.Home, "runner.lock");

    private IBlobClient Blobs => blobs;

    public IDisposable? TryTake()
    {
        Directory.CreateDirectory(paths.Home);
        if (!TakeFile()) return null;
        if (!settings.IsAzure) return new Held(this, null, null);
        var lease = blobs.AcquireLease("runner", 0);
        if (lease is null) { DeleteFile(); return null; }
        var every = int.TryParse(options.Value.RenewSeconds, NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) ? s : 20;
        Timer? keeper = null;
        keeper = new Timer(_ =>
        {
            try { if (!blobs.RenewLease("runner", lease)) keeper?.Dispose(); }
            catch (Exception e) when (e is Core.FactoryException or HttpRequestException) { }
        }, null, TimeSpan.FromSeconds(every), TimeSpan.FromSeconds(every));
        return new Held(this, lease, keeper);
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

    private sealed class Held(RunnerLock owner, string? lease, Timer? keeper) : IDisposable
    {
        private bool _done;

        public void Dispose()
        {
            if (_done) return;
            _done = true;
            keeper?.Dispose();
            if (lease is not null) try { owner.Blobs.ReleaseLease("runner", lease); } catch (Core.FactoryException) { }
            owner.DeleteFile();
        }
    }
}
