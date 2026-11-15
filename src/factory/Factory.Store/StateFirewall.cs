using System.Text.RegularExpressions;
using Factory.Core;
using Factory.Core.Abstractions;
using Factory.Store.Abstractions;
using Microsoft.Extensions.Options;

namespace Factory.Store;

/// <summary>
/// state.ps1's Invoke-StateAllowIp: asks FACTORY_IP_URL (else api.ipify.org) for this machine's public IP, adds it to the
/// account's firewall through <c>az</c> (or FACTORY_AZ_CMD), removes the one added last, and records the new one in the
/// local folder (never synced). The same IP as last time changes nothing.
/// </summary>
public sealed partial class StateFirewall(
    HttpClient http, IProcessRunner processes, IFileStore files, IStateSettings settings, IOptions<StateOptions> options) : IStateFirewall
{
    private StateOptions O => options.Value;

    public AllowIpResult AllowIp()
    {
        var account = settings.Account ?? throw new FactoryException("state: allow-ip needs FACTORY_STORAGE_ACCOUNT or FACTORY_BLOB_URL");
        var ipUrl = string.IsNullOrEmpty(O.IpUrl) ? "https://api.ipify.org" : O.IpUrl;
        string ip;
        try { ip = http.GetStringAsync(ipUrl).GetAwaiter().GetResult().Trim(); }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { throw new FactoryException($"state: {ipUrl} did not answer: {e.Message}"); }
        if (!Ipv4().IsMatch(ip)) throw new FactoryException($"state: {ipUrl} answered '{ip}', not an IPv4 address");

        var dir = O.LocalDir ?? "";
        var record = Path.Combine(dir, "allowed-ip.txt");
        var old = files.ReadText(record)?.Trim() is { Length: > 0 } o ? o : null;
        if (old != ip)
        {
            Az("storage", "account", "network-rule", "add", "--account-name", account, "--resource-group", settings.Group, "--ip-address", ip, "--output", "none");
            if (old is not null)
            {
                try { Az("storage", "account", "network-rule", "remove", "--account-name", account, "--resource-group", settings.Group, "--ip-address", old, "--output", "none"); }
                catch (FactoryException) { }
            }
            files.EnsureDirectory(dir);
            files.WriteAtomic(record, System.Text.Encoding.UTF8.GetBytes(ip + Environment.NewLine));
        }
        return new AllowIpResult(ip, old is not null && old != ip ? old : null, account);
    }

    private void Az(params string[] args)
    {
        var r = string.IsNullOrEmpty(O.AzCmd)
            ? processes.Run(StateToken.AzProgram, args, "")
            : processes.Run(CommandOverride.Parse("FACTORY_AZ_CMD", O.AzCmd)[0], [.. CommandOverride.Parse("FACTORY_AZ_CMD", O.AzCmd).Skip(1), .. args], "");
        if (r.ExitCode != 0) throw new FactoryException($"state: az {string.Join(' ', args.Take(4))} failed: {(r.StdOut + r.StdErr).Trim()}");
    }

    [GeneratedRegex(@"^\d{1,3}(\.\d{1,3}){3}$")] private static partial Regex Ipv4();
}
