# 08: Port the dashboard server

**Status:** not started. **Branch:** `feature/08-port-server`. **Depends on:** 07.

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
