using Factory.Core;

namespace Factory.UnitTests;

public class SpecTextTests
{
    static string[] Lines(string text) => text.Replace("\r", "").Split('\n');

    const string Spec = """
        # Spec: J-004 — Book search

        ## Target
        - Repo: someone/todo-app

        ## Gate
        - build: `dotnet build`
        - ui-e2e: `npm test`
        - not a gate line
        - Bad Name: `x`

        ## Scope
        - In:
          - `src/api/**` (the endpoint)
          - `src/ui/*.tsx`, and `tests/x` is prose
          a continuation line
        - Out: anything else

        ## Acceptance
        - `not/a/glob`
        """;

    [Fact]
    public void Spec_meta_reads_the_repo_and_the_gate_commands()
    {
        var m = SpecText.Meta(Lines(Spec));

        Assert.Equal("someone/todo-app", m.Repo);
        Assert.Equal([new GateCommand("build", "dotnet build"), new GateCommand("ui-e2e", "npm test")], m.Gate);
    }

    [Fact]
    public void Spec_meta_matches_without_case_as_powershell_match_does()
    {
        var m = SpecText.Meta(Lines("## TARGET\n- repo: a/b\n## gate\n- Build: `x`"));

        Assert.Equal("a/b", m.Repo);
        Assert.Equal([new GateCommand("Build", "x")], m.Gate);
    }

    [Fact]
    public void Spec_meta_of_a_spec_with_neither_section_is_empty()
    {
        var m = SpecText.Meta(Lines("# Spec: x"));

        Assert.Null(m.Repo);
        Assert.Empty(m.Gate);
    }

    [Fact]
    public void Scope_in_takes_the_first_backticked_span_of_each_sub_bullet_and_where_a_widening_goes()
    {
        var lines = Lines(Spec);
        var s = SpecText.ScopeIn(lines);

        Assert.Equal(["src/api/**", "src/ui/*.tsx"], s.Globs);
        Assert.Equal("  a continuation line", lines[s.At]);
    }

    [Fact]
    public void A_sub_bullet_with_several_globs_gives_each_one_outside_parentheses()
    {
        var s = SpecText.ScopeIn(Lines("## Scope\n- In:\n  - `CLAUDE.md`, `README.md` (only to drop `BookSeeder`)\n  - `a/vite.config.ts` (the `test` block), `a/vitest.config.ts` (if used)\n- Out: `lib/auth`"));

        Assert.Equal(["CLAUDE.md", "README.md", "a/vite.config.ts", "a/vitest.config.ts"], s.Globs);
    }

    // Cloud J-005 (2026-11-21): "`vite.config.ts` and `vitest.config.ts`" lost its second path to the scope check, and
    // the job blocked after a paid build. Such a line is now refused when the spec is written or approved.
    [Theory]
    [InlineData("  - `a/vite.config.ts` and `a/vitest.config.ts` (test environment)", "a/vitest.config.ts")]
    [InlineData("  - `a/x.cs`, then `src/**` too", "src/**")]
    [InlineData("  - `a/x.cs` plus `README.md`", "README.md")]
    public void A_path_after_other_words_is_an_ambiguous_scope_line(string bullet, string span)
    {
        var lines = Lines($"# Spec: S\n## Scope\n- In:\n{bullet}\n- Out: `lib/auth` and `x/y.cs`");

        Assert.Equal([$"S's Scope line names `{span}` after other words, so the scope check would miss it: give it its own sub-bullet, or comma-join it at the start of the line"],
            SpecText.AmbiguousScope(lines));
    }

    [Theory]
    [InlineData("  - `CLAUDE.md`, `README.md` (only to drop `BookSeeder`)")]
    [InlineData("  - `a/vite.config.ts` (the `test` block), `a/vitest.config.ts` (if used)")]
    [InlineData("  - `src/x.cs` keep the `Seed` method and `config` key")]
    [InlineData("  - `src/x.cs` (rename `a/old.ts` to `a/new.ts`)")]
    public void Comma_runs_parentheses_and_words_that_are_not_paths_are_fine(string bullet)
    {
        Assert.Empty(SpecText.AmbiguousScope(Lines($"# Spec: S\n## Scope\n- In:\n{bullet}\n")));
    }

    [Fact]
    public void A_spec_with_no_in_line_has_no_globs_and_no_insertion_point()
    {
        var s = SpecText.ScopeIn(Lines("## Scope\n- Out: x"));

        Assert.Empty(s.Globs);
        Assert.Equal(-1, s.At);
    }

    [Theory]
    [InlineData("src/api/**", "src/api/Program.cs", true)]
    [InlineData("src/api/**", "src/api/a/b/c.cs", true)]
    [InlineData("src/api/**", "src/apix/a.cs", false)]
    [InlineData("**/package-lock.json", "package-lock.json", true)]
    [InlineData("**/package-lock.json", "src/ui/react/package-lock.json", true)]
    [InlineData("src/ui/*.tsx", "src/ui/App.tsx", true)]
    [InlineData("src/ui/*.tsx", "src/ui/a/App.tsx", false)]
    [InlineData("IaC/auth.*.tf", "IaC/auth.main.tf", true)]
    [InlineData("IaC/auth.*.tf", "IaC/authXmainXtf", false)]
    [InlineData("file?.md", "file1.md", true)]
    [InlineData("file?.md", "file/.md", false)]
    [InlineData("SRC/App.cs", "src/app.cs", true)]
    [InlineData("a**", "abc/def", true)]
    public void Scope_globs_match_one_segment_with_star_and_any_with_double_star(string glob, string path, bool matches)
    {
        Assert.Equal(matches, Glob.Matches(glob, path));
    }

    [Fact]
    public void Strays_are_the_paths_no_glob_matches_in_diff_order()
    {
        Assert.Equal(["IaC/adopt.tf", "README.md"],
            Glob.Strays(["src/**", "IaC/main.tf"], ["src/a.cs", "IaC/adopt.tf", "IaC/main.tf", "README.md"]));
    }

    [Theory]
    [InlineData("## Escalation reason\nfirst\nsecond\n## Next\nx", "first\nsecond")]
    [InlineData("## Escalation reason  \r\n  body  \r\n", "body")]
    [InlineData("## escalation REASON\nbody", "body")]
    [InlineData("## Escalation reason\nN/A\n", null)]
    [InlineData("## Escalation reason\nn/a", null)]
    [InlineData("## Escalation reason\n\n## Next", null)]
    [InlineData("# Escalation reason\nbody", null)]
    [InlineData("text ## Escalation reason\nbody", null)]
    public void A_section_is_its_trimmed_body_and_null_when_absent_empty_or_n_a(string text, string? body)
    {
        Assert.Equal(body, SpecText.Section(text, "Escalation reason"));
    }

    [Fact]
    public void A_section_of_no_file_is_null()
    {
        Assert.Null(SpecText.Section(null, "Template gaps"));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("Verdict: PASS  \r\nrest", "Verdict: PASS")]
    [InlineData("\nsecond", "")]
    [InlineData("only", "only")]
    public void First_line_is_trimmed_at_the_end_and_null_for_an_empty_file(string? text, string? line)
    {
        Assert.Equal(line, SpecText.FirstLine(text));
    }

    [Theory]
    [InlineData("someone/todo-app", null)]
    [InlineData("a-b/c.d_e-f", null)]
    [InlineData(null, "a target repo (owner/name) is required")]
    [InlineData("", "a target repo (owner/name) is required")]
    [InlineData("D:\\code\\x", "target 'D:\\code\\x' is not a GitHub repo written owner/name")]
    [InlineData("a/b/c", "target 'a/b/c' is not a GitHub repo written owner/name")]
    [InlineData("a_b/c", "target 'a_b/c' is not a GitHub repo written owner/name")]
    public void A_target_is_a_github_repo_written_owner_slash_name(string? target, string? refusal)
    {
        Assert.Equal(refusal, SpecText.TargetError(target));
    }

    // Spec 23: a new app's spec decides Home in its own section, as one of three words and a detail.
    [Theory]
    [InlineData("## Home\n- Home: redirect: /books", "redirect: /books")]
    [InlineData("## home\n- home: Replace: a dashboard of shelves", "Replace: a dashboard of shelves")]
    [InlineData("## Home\n<!-- note -->\n- Home: keep: the scaffold's dashboard already shows the books", "keep: the scaffold's dashboard already shows the books")]
    public void A_home_line_under_home_is_the_decision(string body, string decision)
    {
        Assert.Equal(decision, SpecText.HomeDecision(Lines($"# Spec: S\n{body}\n## Risks\n- none")));
    }

    [Theory]
    [InlineData("## Scope\n- In:\n  - `a`")]                                // no Home section
    [InlineData("## Home\n- Home: <replace | redirect | keep>: <detail>")]   // the template's placeholder
    [InlineData("## Home\n- Home: maybe later")]                             // not one of the three words
    [InlineData("## Home\n- Home: redirect:")]                               // no detail
    [InlineData("## Scope\n- Home: redirect: /books")]                       // outside the Home section
    public void Anything_else_is_no_decision(string body)
    {
        Assert.Null(SpecText.HomeDecision(Lines($"# Spec: S\n{body}\n")));
    }
}
