# 08: Port the dashboard server

**Status:** built on its branch (2026-11-12), proven on Windows and Linux. **Branch:**
`feature/08-port-server`. **Depends on:** 07. `Factory.Server` serves the contract's Dashboard API on
Kestrel, loopback only; `serve.ps1` forwards to it when FACTORY_IMPL=dotnet. Each write is one
`factory.ps1` call through `IFactoryCalls` (as the runner's `IFactoryCommands`), so the `{ ok, message }`
text is `serve.ps1`'s and the deploy set still runs in PowerShell; cutover (12) swaps that one seam.
`/api` requests run one at a time, as `serve.ps1`'s did; static reads run alongside. The AppHost runs
the server (port 4317) and a runner, both on .NET. Tests start the implementation `FACTORY_IMPL` names
through `fixtures/serve-process.ps1` (seven launch sites), and every `serve.Tests.ps1` block is tagged
`Ported`. Found on the way, fixed in this branch: the .NET runner could not find `factory/scripts`
when started directly (the AppHost), now `FactoryPaths.FindScripts`, shared with the server; and the
AppHost's Aspire 13.6.4 had CVE-2026-120006 (GHSA-f7vh-h5g3-7jrh, published 2026-11-10), now 13.6.5.
Proven locally:
- Windows: .NET 651 unit, 63 integration (Server 87.0%, Runner 86.3%, Cli 99.0%, Core 99.7%, Store 96.6%);
  seventeen deliberate breaks, one per server rule, each turned a test red, and the unit tests caught
  one real bug Pester could not (`factory.ps1` output on both streams gained a blank line). Pester
  `-Tag Ported` 498 local, 300 azure (+2 skipped) both ways; `-Impl dotnet` 437 local, 235 azure (+1
  skipped); Playwright 41 on .NET (the .NET server seen serving it) and 41 on PowerShell. The AppHost
  run against a scratch state: the server answered on 4317 and the runner drained.
- Linux (WSL): `-Tag Ported` 498 local, 300 azure (+2 skipped) both ways, but neither of two full runs
  was clean. The first failed the whole `idea session on a new target` block (local, .NET), which then
  passed alone. The second passed every test, but Pester's TestDrive cleanup failed after the
  `serve.ps1 PR sync` unit (local, PowerShell, which this branch does not change; the same test failed
  in 07's first Linux run). That is the fourth whole-unit failure under WSL load (after 06's `abandon`,
  11's `human moves from ready` and 07's `ideas and specs`): the harness flake is recurring, and should
  be diagnosed before cutover (12) makes Linux a first-class target.

## Goal

`Factory.Server`, an ASP.NET Core minimal API with the exact endpoint table in the contract
("Dashboard API"), serving today's `factory/dashboard/index.html` unchanged until 17.

## Scope

- Every endpoint answers with the contract's `{ ok, message }` shape and status codes (409 while
  `canary.lock` is held, and so on). The endpoints stay **thin wrappers**: they call the same
  application services the CLI uses, with no transition logic in the server.
- A random localhost port by default, `--port` to fix one, and the one-dashboard-per-state-dir lock
  (`serve.lock`).
- `/state/...` static reads (evals, lessons markdown), `state-pull jobs/<id>/` when a job is opened
  on the azure backend, and `sweep` at startup.
- Idea-session endpoints call the existing `idea-turn.ps1` until 10 ports it.
- Bound to loopback only. Auth arrives with cloud mode (18).

## Tests

`serve.Tests.ps1` passes against the .NET server (its helper starts whichever implementation
`FACTORY_IMPL` names). The Playwright check against `simulate.ps1` passes against both.

## Done when

`pwsh factory/scripts/serve.ps1` launches the .NET server (forwarding), the AppHost runs
server and runner together, and both suites are green.
