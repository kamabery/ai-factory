using Factory.Core;

namespace Factory.UnitTests;

/// <summary>Which file a gate line's program starts: on Windows, PATH with PATHEXT (so npm is npm.cmd); elsewhere the OS finds it.</summary>
public class ProgramPathTests
{
    private const string Ext = ".COM;.EXE;.BAT;.CMD";
    private static readonly string A = Path.Combine("C:", "a");
    private static readonly string B = Path.Combine("C:", "b");
    private static readonly string Path2 = A + Path.PathSeparator + B;

    private static string Resolve(string program, params string[] existing) =>
        ProgramPath.Resolve(program, Path2, Ext, windows: true, f => existing.Contains(f, StringComparer.OrdinalIgnoreCase));

    [Fact]
    public void Finds_the_first_PATH_folder_holding_the_program_with_a_PATHEXT_extension() =>
        Assert.Equal(Path.Combine(B, "npm.cmd"), Resolve("npm", Path.Combine(B, "npm.cmd"), Path.Combine(B, "npm")));

    [Fact]
    public void Prefers_the_earlier_extension_in_PATHEXT_within_a_folder() =>
        Assert.Equal(Path.Combine(A, "tool.exe"), Resolve("tool", Path.Combine(A, "tool.cmd"), Path.Combine(A, "tool.exe")));

    [Fact]
    public void A_name_with_an_extension_is_looked_up_as_it_is() =>
        Assert.Equal(Path.Combine(A, "x.cmd"), Resolve("x.cmd", Path.Combine(A, "x.cmd")));

    [Fact]
    public void A_path_is_tried_as_given_then_with_each_extension() =>
        Assert.Equal(Path.Combine("tools", "gate.cmd"), Resolve(Path.Combine("tools", "gate"), Path.Combine("tools", "gate.cmd")));

    [Fact]
    public void A_program_found_nowhere_is_left_for_the_start_to_refuse() => Assert.Equal("nope", Resolve("nope"));

    [Fact]
    public void Off_Windows_the_name_is_left_to_the_OS() =>
        Assert.Equal("npm", ProgramPath.Resolve("npm", Path2, Ext, windows: false, _ => true));
}
