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
Killing `serve.ps1` alone (not its tree) orphaned the forwarded server, so `serve.ps1` now passes
`--parent-pid` and the server's `ParentWatch` stops it, releasing `serve.lock`, about a second after that
process ends (a reused PID with another start time does not count).
Proven locally:
- Windows: .NET 655 unit, 63 integration (Server 86.1%, Runner 86.3%, Cli 99.0%, Core 99.7%, Store 96.6%);
  nineteen deliberate breaks, one per server rule, each turned a test red, and the unit tests caught
  one real bug Pester could not (`factory.ps1` output on both streams gained a blank line). Pester
  `-Tag Ported` 498 local, 300 azure (+2 skipped) both ways; `-Impl dotnet` 437 local, 235 azure (+1
  skipped); Playwright 41 on .NET (the .NET server seen serving it) and 41 on PowerShell.
  After the parent watch, `serve.Tests.ps1` 43 on each implementation and its new orphan test on Linux
  too; one `test.ps1` run showed 62 of 63 integration tests, which five more runs did not repeat. The AppHost
  run against a scratch state: the server answered on 4317 and the runner drained.
- Linux (WSL): `-Tag Ported` 498 local, 300 azure (+2 skipped) both ways, with one under-load
  failure in each of two runs, in different blocks, each passing alone. Kurt accepted that risk
  (2026-11-12): one-off Linux flakes are re-run, not chased. `serve.ps1 PR sync` (runner.Tests),
  the one block that failed twice, was removed; the server's Watchdog unit tests cover its trigger
  and `factory.Tests.ps1` covers `sync-prs`.
