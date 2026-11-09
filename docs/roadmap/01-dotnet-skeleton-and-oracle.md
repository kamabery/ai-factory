# 01: .NET skeleton, CI and the oracle switch

**Status:** built on its branch (2026-11-09), not yet merged. Proven locally: 41 .NET tests (33 unit, 8
integration), gates Core 100%, Store 100%, Cli 92%; `status` Pester blocks pass with `FACTORY_IMPL=ps`
and `dotnet`; the full Pester suite passes (436 local, 236 azure); `aspire run` starts the AppHost.
PR #8. **Branch:** `feature/01-dotnet-skeleton`. **Depends on:** 19.

**Deviations (2026-11-09):**
- One switch, `FACTORY_IMPL`, default `ps`. The Pester helpers keep calling `factory.ps1`, which
  forwards ported commands when it is `dotnet`; the default flips at cutover (12).
- Tests run on Microsoft.Testing.Platform (xUnit v3 4.x requires it on the .NET 10 SDK; `global.json`
  at the repo root), so coverage is Microsoft.Testing.Extensions.CodeCoverage, not coverlet.
- No CI (Kurt, 2026-11-09): the repo has no test workflows, and tests run locally before a PR
  (`docs/TESTING.md`, "Before a PR"). "On all three OSes in CI" is dropped: Windows, locally.
- Found mid-port: the empty board's key order was random per process (an unordered hashtable).
  Fixed in PowerShell and the contract first: `version`, `ideas`, `jobs`.

## Goal

A .NET 10 solution beside `factory/scripts`, built and tested on three OSes, and a Pester suite that
can run against either implementation. One trivial command proves the loop.

## Scope

- `src/factory/Factory.slnx` with empty projects: `Factory.Core`, `Factory.Store`, `Factory.Cli`
  (packed as the `factory` dotnet tool), `Factory.Server`, `Factory.Runner`, `Factory.AppHost`
  (Aspire), `Factory.ServiceDefaults`, `Factory.UnitTests` and `Factory.IntegrationTests` (xUnit v3).
- **Unit-first, per `docs/TESTING.md`:** NSubstitute, AutoFixture, `AutoFixture.AutoNSubstitute` and
  `AutoFixture.Xunit3`, the shared `[AutoNSubData]` attribute, `Microsoft.Extensions.DependencyInjection`
  with a composition root in `Factory.Cli`, and its composition-root test (`ValidateOnBuild`,
  `ValidateScopes`). coverlet with the gates (90% Core, 70% elsewhere) and the tier counts in the
  run summary.
- **The oracle switch:** the Pester helpers that call `factory.ps1` (`F` in `factory.Tests.ps1`, and
  the equivalents in `runner`, `serve`, `state-factory` tests) run `dotnet <Factory.Cli.dll>` when
  `FACTORY_IMPL=dotnet`. Stdout, stderr and the exit code must match the contract exactly.
- **Strangler routing (D3):** `factory.ps1` keeps a list of ported commands and forwards them to the
  .NET CLI with the same arguments and environment (`FACTORY_HOME`, `FACTORY_WORK`,
  `FACTORY_ENV_FILE`, `FACTORY_REPO`, the test overrides). An env switch turns forwarding off, so
  the PowerShell path stays testable until cutover.
- **First ported command: `status [-Json]`.** It is read-only and touches the board format, so it
  proves path resolution (`FACTORY_HOME`, else `<main checkout>/.factory-runs` through
  `git rev-parse --git-common-dir`) and JSON shape.
- ~~**CI**~~ (superseded: no CI, see "Deviations") (`suites.yml`, beside the Playwright smoke set): unit tests on `windows-latest`,
  `ubuntu-latest` and `macos-latest`; integration tests on `ubuntu-latest` (all three in
  `full.yml`). `run-all.ps1 -Tag Ported` runs the ported commands' `Describe` blocks with
  `FACTORY_IMPL=ps` and `dotnet` on Windows and Linux, in place of the 4 shards on port PRs; the
  full Pester suite stays in `full.yml` and on PRs that change only PowerShell.

## Done when

- `status` Pester tests pass under both implementations (locally, on Windows; no CI).
- `status` has unit tests on its service (fakes via `[AutoNSubData]`) and one integration test of
  the CLI's exact output, each seen red, then green, and the coverage gates pass.
- `aspire run` starts the (empty) AppHost.
- The contract has a short "Implementations" note: what `FACTORY_IMPL` does and what forwarding
  means.
