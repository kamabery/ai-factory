# 12: Cutover, retire PowerShell

**Status:** built on its branch (2026-11-14), proven on Windows and Linux, deploys off. **Branch:** `feature/12-cutover`.
**Depends on:** 04–11. The `factory` CLI is the control plane (`factory runner`, `serve`, `idea` replace the scripts; the
plugin and its hooks call it by name); the canary fingerprint hashes the .NET deploy code; `FACTORY_GATE_SHELL` names
the gate's shell, the one PowerShell a job still needs. Pester is retired: [12-pester-mapping.md](12-pester-mapping.md)
maps all 387 cases (18 new tests); the stubs are `Factory.TestHost`. Kept until 16: `setup-azure.ps1`,
`setup-platform.ps1`, `env-file.ps1` and their Pester; `simulate.ps1` stays for Playwright. Suites: .NET 862 unit, 100
integration, gates green, on Windows and in WSL; Playwright 41/41; Pester left 30/30. Live proof: J-001 on
kamabery/todoapp-two (PR #17) ran in WSL with no pwsh on PATH, a local state dir and no HCP_ORG; round 1 escalated on
a missing git identity (an environment gap, logged as a factory lesson), round 2 reached Ready, $2.37; eval recorded.

## Scope

1. The full Pester suite is green with `FACTORY_IMPL=dotnet` on Windows, Linux and macOS (by hand: no CI), with
   no PowerShell fallbacks left, and so are the integration tests on all three and the whole
   Playwright suite.
2. **The plugin calls `factory`**: agents, skills and hooks (`factory/plugin/**`) replace
   `pwsh -File …/factory.ps1` with `factory`, and `hook-event.ps1` becomes `factory event` reading
   the hook JSON from stdin. Update the permission allow rules.
3. Delete `factory/scripts/*.ps1` except the ones moving to 16 (`setup-azure.ps1`,
   `setup-platform.ps1`, kept until then) and `simulate.ps1` (ported to a test fixture, or kept for
   Playwright).
4. Pester is retired: each remaining Pester case is covered by a unit or integration test, pushed
   down per `docs/TESTING.md` rather than copied 1:1. Prove that with a mapping file in the PR, then delete `factory/tests/*.Tests.ps1`. Stubs move to an ASP.NET test
   host.
5. **The canary fingerprint** hashes `deploy.ps1`, `hcp.ps1` and `github.ps1` under both implementations (contract,
   "Implementations"). Before those go, point it at the .NET deploy code (`DeployService`, `CanaryService`,
   `HcpClient`, `GitHubClient`, `ToolRunner`), or a change to the deploy path stops invalidating the canary.
6. Rewrite `CLAUDE.md`'s Commands, `docs/BUILD-GUIDE.md`, and the contract's command-table heading
   (`factory.ps1` becomes `factory`).

## Done when

A real job runs end to end with no PowerShell on the path (test on Linux), with deploys off (16
proves previews), and its eval recorded.
