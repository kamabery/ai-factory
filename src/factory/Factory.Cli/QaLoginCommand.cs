using System.Text.Json.Nodes;
using Factory.Cli.Abstractions;
using Factory.Core;
using Factory.Store.Abstractions;

namespace Factory.Cli;

/// <summary>
/// <c>qa-login &lt;id&gt;</c>: the live preview's QA user and password, from its workspace's outputs (the auth add-on's), for the
/// human only. The recorded workspaceId is enough, so a dashboard with deploys off can still log in.
/// </summary>
public sealed class QaLoginCommand(IHcpClient hcp) : ICommand
{
    public string Name => "qa-login";

    public CommandResult Run(CommandArgs args, IBoardSession session)
    {
        var j = session.Board.Job(args.Arg(1));
        if (j.Node["deploy"] is not JsonObject d || !PsJsonReader.Is(PsJsonReader.Str(d["status"]), "up"))
            throw new FactoryException($"qa-login refused: {j.Id} has no live preview");
        var wsId = PsJsonReader.Str(d["workspaceId"]) is { Length: > 0 } known ? known : hcp.WorkspaceId(PsJsonReader.Text(d["workspace"]));
        var outs = string.IsNullOrEmpty(wsId) ? new Dictionary<string, string?>() : hcp.Outputs(wsId, ["qa_password"]);
        if (string.IsNullOrEmpty(outs.GetValueOrDefault("qa_user"))) throw new FactoryException("qa-login refused: the preview has no QA user (no auth add-on)");
        return CommandResult.Ok($"user: {outs["qa_user"]}", $"password: {outs.GetValueOrDefault("qa_password")}");
    }
}
