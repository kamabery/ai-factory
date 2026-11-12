# 07: Port the runner and run-mode providers

**Status:** built on its branch (2026-11-11), proven on Windows and Linux, and live-proven
(2026-11-12) on todoapp-two with deploys off: J-033 in local mode (subscription, $1.83, 6 min, PR
todoapp-two #13) and J-034 in cloud mode (Foundry `fdy-factory-gc6c2b`, keyless, $1.66, 6 min, PR
todoapp-two #14), both on the .NET runner, first round green, evals recorded (both good). **Branch:**
`feature/07-port-runner`. **Depends on:** 06, 11. `Factory.Runner` is a hosted service porting
`runner.ps1` and `claude-run.ps1`; `RunnerLock` in `Factory.Store` holds `runner.lock` and, on the
azure backend, the `locks/runner` lease and its renewal. `runner.ps1` forwards to it when
FACTORY_IMPL=dotnet, defaulting `Factory__Mode` to `local`. `runner.Tests.ps1`'s runner contexts
(all but `hook-event`) are tagged `Ported`. Proven locally:
- Windows: .NET 597 unit, 55 integration (Core 99.7%, Store 96.7%, Cli 99.0%, Runner 86.2%); fourteen
  deliberate breaks, one per runner rule, each turned a test red. The canary-once break at first
  hung its test instead (the fake factory asked for a canary forever, and grew to 19 GB); the fake now
  fails a drain past 1000 calls. Pester `-Tag Ported` 414 local both ways; `-Impl dotnet` 437 local,
  235 azure (+1 skipped); Playwright on .NET 41. The runner's Pester blocks run about 3x slower on
  .NET than on PowerShell (the `runner` context 899s against 335s).
- Linux (WSL): `-Tag Ported` 414 local, 300 azure (+2 skipped) both ways. The first run failed the
  whole `ideas and specs` unit (local, .NET) and the `serve.ps1 PR sync` test (local, PowerShell);
  the block passed alone, and a second full run was green. That is a third whole-unit setup failure
  on Linux under load (after 06's `abandon` and 11's `human moves from ready`): the harness flake is
  recurring and needs diagnosing.

## Goal

`Factory.Runner`, a hosted service that replaces `runner.ps1` and `claude-run.ps1`, and
`ILlmProvider`, which replaces `llm.ps1`.

## Scope

- **Draining:** the Approved queue one job at a time (`next`, the canary before a deployable job,
  `checkout`, `claude -p` with the plugin, `publish`, `set-cost`, `lesson`, `sweep` when drained).
  The single-runner guarantee moves from the PowerShell lock to the store.
- **`ILlmProvider`** is resolved once from `Factory:Mode` (contract "Run mode and providers"):
  `local` sets the child `claude` environment for the subscription; `cloud` sets
  `CLAUDE_CODE_USE_FOUNDRY=1`, `ANTHROPIC_FOUNDRY_RESOURCE`, and the pinned
  `ANTHROPIC_DEFAULT_*_MODEL` deployment names, with no API key. In .NET, `appsettings.Development.json`
  sets `local`, and an unset mode outside Development fails at startup.
- The shell limit (`BASH_MAX_TIMEOUT_MS`), the cost and time from the `claude` result, the
  `usage-limit` flag from 00, and the `cost` event's `provider`.
- `serve.ps1` starts the .NET runner instead of `runner.ps1` (forwarding).
- Deploy is not ported yet (09 runs after 10): the runner calls `canary`, `next -NeedsCanary`,
  `sweep` and the deploy commands through `factory.ps1`, which runs them in PowerShell (D3).

## Tests

`runner.Tests.ps1` passes both ways through `tool-stub.ps1`, which stands in for `claude`. xUnit
covers provider resolution for each mode, plus the failure on an unset mode.

## Done when

The suites are green, and one real job runs end to end on the .NET runner in local mode and one in
cloud mode, both with deploys off (README rules) (Foundry account `fdy-factory-gc6c2b`), each with an eval recorded.
