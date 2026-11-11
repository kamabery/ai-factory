using System.Text.RegularExpressions;

namespace Factory.Core;

/// <summary>factory.ps1's Quote-NativeArgs, for gate commands run through <c>pwsh -Command</c>.</summary>
public static partial class NativeArgs
{
    /// <summary>
    /// pwsh splits an unquoted native argument like <c>-var-file=envs/ephemeral.tfvars</c> at the dot (J-022), so such a
    /// token is quoted. No cmdlet takes <c>-name=value</c>, so this only ever touches native arguments.
    /// </summary>
    public static string Quote(string cmd) => DottedArg().Replace(cmd, "\"$1\"");

    [GeneratedRegex(@"(?<=^|[\s;|(])(-[A-Za-z][\w-]*=[^\s""'`;|()]*\.[^\s""'`;|()]*)(?=$|[\s;|)])")] private static partial Regex DottedArg();
}
