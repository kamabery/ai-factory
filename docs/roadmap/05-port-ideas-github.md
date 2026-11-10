# 05: Port the idea and spec commands, and the GitHub client

**Status:** built on its branch (2026-11-10), not yet merged. **Branch:** `feature/05-port-ideas`. **Depends on:** 04.
Seven commands forwarded: `new-idea`, `add-spec`, `idea-done`, `idea-abandon`, `check-target`,
`clone-target` and `list-repos`. Proven locally, under the new port rule (`docs/TESTING.md`):
- 498 .NET tests (453 unit, 45 integration, the new ones against `github-stub.ps1`), gates Core
  99.1%, Store 98.3%, Cli 98.4%; twelve deliberate breaks, one per command rule, each turned a test red.
- The 16 `Ported` blocks pass both ways, local and azure (`ideas and specs`, `abandon` and `targets`
  are new). The whole suite on .NET (`-Impl dotnet`): 437 local, 235 azure. Playwright on .NET: 41.

What differs from github.ps1, on purpose:
- `GitHubClient` sends a `User-Agent`: GitHub refuses a request without one, and `Invoke-RestMethod`
  always sent its own.
- `ProcessRunner` sets `GIT_TERMINAL_PROMPT=0` for every child, so the CLI run without `factory.ps1`
  (which sets it in github.ps1) never waits on a credential prompt either.
- `IGitHubCredential` is the seam 14 needs: `PatCredential` reads `GITHUB_TOKEN`; the API's bearer
  header and git's `x-access-token` header both come from it.
- `IGitHub` has what these commands need (`Send`, `Lookup`, `OwnedRepos`, `CloneUrl`, `GitAuth`).
  `New-GitHubRepo`, `Get-Pr` and `Get-OpenPr` come with 06 (checkout, publish, merge, sync-prs);
  `discard` still waits for 09 (destroy).

Found on the way: plain `run-all.ps1` (no `-Impl`) had failed at discovery since 04 (PR #12): its
loop variable `$impl` is the `-Impl` parameter, whose `ValidateSet` refused `''`. Fixed here.
`idea.ps1` was not run live (it opens an interactive session); the commands it calls are covered above.

## Commands

`new-idea` (`-Target`, `-Scaffold`, `-Base`), `add-spec`, `idea-done` (`-Failed -Reason`),
`idea-abandon`, `check-target`, `clone-target`, `list-repos`, and the scaffold helpers
(`react`, `blazor`, `blazor-maui`).

## The GitHub client

Port `github.ps1` (`Invoke-GitHub`, `GhError`, `PatNeeds`) to a typed `IGitHub` client over
`HttpClient`. Today it uses the GitHub PAT from `.env` (`GITHUB_TOKEN`). Keep the interface
credential-agnostic, so 14 can swap in GitHub App installation tokens without touching callers.
`GhError`'s permission diagnosis (the `github-permission` flag) is part of the contract and must
print the same text.

The GitHub stub (`factory/tests/fixtures/github-stub.ps1`) stays the test server.

## Done when

The idea and spec `Describe` blocks pass both ways. `idea.ps1` (the CLI fallback) and the
dashboard's idea session still work through forwarding.
