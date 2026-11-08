# 01: .NET skeleton, CI and the oracle switch

**Status:** not started. **Branch:** `feature/01-dotnet-skeleton`. **Depends on:** 00.

## Goal

A .NET 10 solution beside `factory/scripts`, built and tested on three OSes, and a Pester suite that
can run against either implementation. One trivial command proves the loop.

## Scope

- `src/factory/Factory.slnx` with empty projects: `Factory.Core`, `Factory.Store`, `Factory.Cli`
  (packed as the `factory` dotnet tool), `Factory.Server`, `Factory.Runner`, `Factory.AppHost`
  (Aspire), `Factory.ServiceDefaults`, `Factory.Tests` (xUnit).
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
- **CI:** extend the 00 workflow to a matrix `windows-latest`, `ubuntu-latest` and `macos-latest`
  running `dotnet test` and Pester with `FACTORY_IMPL=dotnet` for the ported commands' `Describe`
  blocks (tag them `Ported`). Full PowerShell Pester stays on Windows only.

## Done when

- `status` Pester tests pass under both implementations, and on all three OSes in CI.
- `dotnet test` has `status` tests, seen red, then green.
- `aspire run` starts the (empty) AppHost.
- The contract has a short "Implementations" note: what `FACTORY_IMPL` does and what forwarding
  means.
