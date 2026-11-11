using Factory.Cli.Abstractions;
using Factory.Core;
using Factory.Store.Abstractions;

namespace Factory.Cli;

/// <summary><c>allow-ip</c>: opens the state storage account's firewall to this machine's public IP, closing the last one.</summary>
public sealed class AllowIpCommand(IStateSettings settings, IStateFirewall firewall) : ICommand
{
    public string Name => "allow-ip";

    public CommandResult Run(CommandArgs args, IBoardSession session)
    {
        if (!settings.IsAzure) throw new FactoryException("allow-ip refused: FACTORY_STATE is not azure");
        var r = firewall.AllowIp();
        return CommandResult.Ok($"allowed {r.Ip} on {r.Account}" + (r.Removed is null ? "" : $"; removed {r.Removed}"));
    }
}
