# 05: Port the idea and spec commands, and the GitHub client

**Status:** not started. **Branch:** `feature/05-port-ideas`. **Depends on:** 04.

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
