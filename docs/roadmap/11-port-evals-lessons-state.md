# 11: Port evals, lessons and state admin

**Status:** built on its branch (2026-11-11), proven on Windows and Linux. **Branch:**
`feature/11-port-evals`. **Depends on:** 04. Ten commands forwarded: the eight below, plus `discard`
and `resume`, which no spec had taken (04 left `discard` for its destroy, which 06's detached
launcher now starts; `resume` is a plain board command). `allow-ip` stays on `az` (or
FACTORY_AZ_CMD), as `state.ps1` ran it; its last IP lives in FACTORY_LOCAL_DIR, else the OS's local
app-data folder. `state-migrate` opens the board unrefreshed (`ICommand.RefreshesBoard`).
Proven locally:
- Windows: .NET 571 unit, 52 integration (Core 99.7%, Store 97.8%, Cli 98.9%); 21 deliberate breaks,
  one per command rule, each turned a test red (two did only after the tests they lacked were added:
  the eval's cost rounding, and reading `RefreshesBoard` through `ICommand`). Pester `-Tag Ported` 332
  local, 300 azure (+2 skipped) both ways; `-Impl dotnet` 437 local, 235 azure (+1 skipped);
  Playwright on .NET 41.
- Linux (WSL): .NET 571 unit, 52 integration; `-Tag Ported` 332 local, 300 azure (+2 skipped) both
  ways. One earlier run failed the whole `human moves from ready` unit (azure, PowerShell) at setup;
  it did not repeat. With spec 06's `abandon` blip that is two whole-unit setup failures on Linux under
  load: a harness flake to diagnose if it comes back.

## Commands

`eval`, `feedback`, `lesson` (incl. the build.md template-gap compile and the `- None.` filter),
`lesson-mark`, `state-migrate`, `state-pull`, `prune`, and `allow-ip` (through `az`, or the ARM API
with `Azure.Identity`; the last-added IP is recorded in the OS app-data folder, not
`%LOCALAPPDATA%` literally).

## Tests

Their `Describe` blocks pass both ways, local and azure.

## Done when

Every `factory.ps1` command but spec 09's deploy set is forwarded: the forwarding list equals the
command table less `deploy`, `deploy-log`, `deploy-fix`, `block`, `redeploy`, `destroy`, `sweep`,
`canary`, `canary-destroy` and `qa-login`. (The first wording, "every command", predates the
2026-11-09 reorder that moved 09 after this spec.)
