# 07: Port the runner and run-mode providers

**Status:** not started. **Branch:** `feature/07-port-runner`. **Depends on:** 06, 11.

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
