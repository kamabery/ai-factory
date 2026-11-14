using System.Runtime.InteropServices;
using SQLitePCL;

namespace Factory.Data;

/// <summary>
/// Chooses the SQLite library before anything opens a connection (contract, "State backends", Database): EF Core's
/// e_sqlite3, or, where that cannot load (it needs glibc 2.33+; Ubuntu 20.04 has 2.31), the OS's libsqlite3.so.0.
/// </summary>
public static class SqliteNative
{
    private static readonly Lazy<string> Loaded = new(Load);

    /// <summary><c>e_sqlite3</c> or <c>sqlite3</c> (the OS's).</summary>
    public static string Library => Loaded.Value;

    public static void Init() => _ = Loaded.Value;

    private static string Load()
    {
        try
        {
            raw.SetProvider(new SQLite3Provider_e_sqlite3());
            _ = raw.sqlite3_libversion_number();   // the first call loads the native library
            return "e_sqlite3";
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException) { }

        // The provider asks for "sqlite3"; a distro without the -dev package has only the versioned name.
        NativeLibrary.SetDllImportResolver(typeof(SQLite3Provider_sqlite3).Assembly, (name, _, _) =>
            name == "sqlite3" && NativeLibrary.TryLoad("libsqlite3.so.0", out var handle) ? handle : IntPtr.Zero);
        raw.SetProvider(new SQLite3Provider_sqlite3());
        _ = raw.sqlite3_libversion_number();
        return "sqlite3";
    }
}
