using System.Diagnostics;
using Factory.Cli.Abstractions;
using Factory.Core;
using Factory.Core.Abstractions;

namespace Factory.Cli;

/// <summary>The system browser (the OS's handler for http URLs), or FACTORY_BROWSER_CMD in its place.</summary>
public sealed class SystemBrowser(CliEnvironment host, IProcessRunner processes) : IBrowser
{
    public void Open(string url)
    {
        if (host.Get("FACTORY_BROWSER_CMD") is { } cmd)
        {
            var fake = CommandOverride.Parse("FACTORY_BROWSER_CMD", cmd);
            processes.Run(fake[0], [.. fake.Skip(1), url], "");
            return;
        }
        try
        {
            if (OperatingSystem.IsWindows()) Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
            else Process.Start(new ProcessStartInfo("xdg-open", [url]) { UseShellExecute = false })?.Dispose();
        }
        catch (System.ComponentModel.Win32Exception) { }   // no browser here: init has printed the URL
    }
}
