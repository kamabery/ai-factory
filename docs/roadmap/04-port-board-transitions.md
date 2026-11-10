# 04: Port the board and transition commands

**Status:** built on its branch (2026-11-10), not yet merged. Nine commands forwarded: `approve`,
`edit-spec`, `rework`, `purge`, `set-stage`, `set-review`, `read-reviews`, `read-decision` and
`event`. Proven locally:
- 422 .NET tests (383 unit, 39 integration), gates Core 99.0%, Store 97.4%, Cli 98.0%; ten deliberate
  breaks, one per command rule, each turned a test red.
- The 13 `Ported` blocks of `factory.Tests.ps1` pass both ways, local and azure.
- The **whole** Pester suite passes with every forwarded command on .NET (`run-all.ps1 -Impl dotnet`,
  new): local 437/437, azure 235 + 1 skipped. That run covers every other caller: the runner,
  `serve.ps1`, deploy and gate.
- A new test in `canary.Tests.ps1` passes under both implementations and fails when the .NET
  fingerprint differs from `factory.ps1`'s.
- Playwright with `FACTORY_IMPL=dotnet`: smoke 5/5, the whole suite 41/41. A missing CLI fails them,
  so they did reach .NET.

**Branch:** `feature/04-port-board`. **Depends on:** 03.

**Deviations (2026-11-10):**
- **`discard` stays in PowerShell.** It closes the job's PR and deletes its branch on GitHub (the
  GitHub client is 05) and starts the preview's destroy (09). It moves with whichever of those lands
  last. Its `Describe` blocks run unchanged.
- **The canary fingerprint is ported here**, ahead of 09. `set-stage approved->build` checks the
  queue, and when a job behind a held deployable job asks to build, the queue reads `canary.json`
  against the fingerprint. That fingerprint hashes the deploy scripts, the templates and the HCP
  variable set (`IHcpClient`, one GET so far). `factory.ps1` passes its own folder as
  `FACTORY_SCRIPTS`.
- **Only `factory.ps1`'s own parameter names bind as parameters.** Any other token that starts with
  `-` (a rework note can) stays positional, as PowerShell's binding leaves it.
- **Event numbers stay as written.** `factory.ps1`'s `ConvertFrom-Json` / `ConvertTo-Json` round
  trip can respell a number (`1.50` as `1.5`). The CLI keeps the event's own text. Readers parse it,
  so nothing depends on the spelling.
- **"Three OSes"** is Windows, locally (tests run before a PR, never in CI).

## Commands

`approve`, `rework` (incl. `Scope +:` notes and `rework-<n>.md`), `discard` (state side; the
GitHub calls stay forwarded to PowerShell until 05/06, or are ported here behind an interface),
`purge`, `set-stage`, `set-review`, `read-reviews`, `read-decision`, `event` (used by
`hook-event.ps1`), `edit-spec`.

## How

Each is a shell over `Factory.Core` plus `IFactoryStore`. Add it to the forwarding list as soon as
its Pester `Describe` passes under `FACTORY_IMPL=dotnet`, then write its xUnit tests. Watch the
case-insensitivity hazard in reverse: C# is case-sensitive where PowerShell was not, so JSON
property lookups must match the contract's casing.

## Done when

The `Describe` blocks for these commands in `factory.Tests.ps1` pass both ways, local and azure, on
three OSes. Playwright against `simulate.ps1` still passes, since `serve.ps1` now reaches .NET
through forwarding.
