using Factory.Core;
using Factory.Core.Abstractions;
using Factory.Store.Abstractions;

namespace Factory.Cli;

/// <summary>
/// init's template workspace step: a tool install clones FACTORY_TEMPLATES_REPO into its profile (contract, "Installs"), as
/// the App's installation on the repo's owner when there is one, else anonymously.
/// </summary>
public sealed class TemplateWorkspace(IFactoryPaths paths, IFileStore files, IGitClient git, IDotEnv env, IGitHub github, IGitHubCredential credential)
{
    public const string DefaultRepo = "kamabery/template-projects";

    public void Ensure(TextWriter output)
    {
        // Only the profile's own default: a checkout install, or a FACTORY_TEMPLATES set by hand, is left alone.
        if (paths.Profile is not { } profile) return;
        var root = paths.TemplatesRoot;
        if (!string.Equals(root, Path.Combine(profile, "template-projects"), StringComparison.Ordinal)) return;
        if (files.DirectoryExists(root))
        {
            output.WriteLine($"ok    templates: {root}");
            return;
        }
        var repo = env.Get("FACTORY_TEMPLATES_REPO") ?? DefaultRepo;
        files.EnsureDirectory(profile);
        var owner = repo.Split('/')[0];
        string? token = null;
        try { token = credential.Token(owner); }
        catch (FactoryException) { }   // the App is not installed on that owner: anonymously
        // As the App when it is installed there (a private templates repo, or a fork); never through a credential helper,
        // which in a fresh VM would open a sign-in.
        string[] auth = token is null ? ["-c", "credential.helper="] : [.. github.GitAuth(owner)];
        var r = git.Run(profile, [.. auth, "clone", github.CloneUrl(repo), root]);
        if (r.Code != 0) throw new FactoryException($"init: could not clone {repo} to {root}: {r.Out}");
        output.WriteLine($"ok    templates: cloned {repo} to {root}");
    }
}
