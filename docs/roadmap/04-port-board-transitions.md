# 04: Port the board and transition commands

**Status:** not started. **Branch:** `feature/04-port-board`. **Depends on:** 03.

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
