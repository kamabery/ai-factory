using System.Security.Cryptography;
using System.Text;

namespace Factory.Store;

/// <summary>
/// factory.ps1's hash of a state folder: SHA-256 of its lower-cased path, the first 16 hex digits. It names the
/// board mutex (<c>factory-board-&lt;hash&gt;</c>), so both implementations take the same one.
/// </summary>
public static class StateHash
{
    public static string Of(string home) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(home.ToLowerInvariant())))[..16];
}
