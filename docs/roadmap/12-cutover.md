# 12: Cutover, retire PowerShell

**Status:** not started. **Branch:** `feature/12-cutover`. **Depends on:** 04–11.

## Scope

1. The full Pester suite is green with `FACTORY_IMPL=dotnet` on Windows, Linux and macOS in CI, with
   no PowerShell fallbacks left.
2. **The plugin calls `factory`**: agents, skills and hooks (`factory/plugin/**`) replace
   `pwsh -File …/factory.ps1` with `factory`, and `hook-event.ps1` becomes `factory event` reading
   the hook JSON from stdin. Update the permission allow rules.
3. Delete `factory/scripts/*.ps1` except the ones moving to 16 (`setup-azure.ps1`,
   `setup-platform.ps1`, kept until then) and `simulate.ps1` (ported to a test fixture, or kept for
   Playwright).
4. Pester is retired: each remaining Pester case already has an xUnit counterpart. Prove that with
   a mapping file in the PR, then delete `factory/tests/*.Tests.ps1`. Stubs move to an ASP.NET test
   host.
5. Rewrite `CLAUDE.md`'s Commands, `docs/BUILD-GUIDE.md`, and the contract's command-table heading
   (`factory.ps1` becomes `factory`).

## Done when

A real job runs end to end with no PowerShell on the path (test on Linux), with deploys off (16
proves previews), and its eval recorded.
