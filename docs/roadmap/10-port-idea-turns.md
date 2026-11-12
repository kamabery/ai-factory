# 10: Port idea turns onto the run mode

**Status:** built on its branch (2026-11-12), proven on Windows and Linux, and live-proven. **Branch:**
`feature/10-port-idea-turns`. **Depends on:** 07, 08. Every idea turn is now `factory idea-turn <S>` (the .NET CLI),
which runs one `claude -p` pass on the run mode's provider, exactly as a job does, with the eight idea tools as a stdio
MCP server (`factory idea-mcp <S>`) and no built-in tools; later turns `--resume` the turn's claude session, and the
prompt goes on stdin. `idea-turn.ps1` forwards to it under every `FACTORY_IMPL`. The Messages-API loop, its Entra token
code and `ANTHROPIC_API_KEY` are gone: both dashboards enable idea sessions locally with no key. Moved for sharing:
`LlmProvider` to Store (jobs and idea turns), the runner's lock as `LeasedLock` (runner.lock and turn.lock), and
`ClaudeCli.Executable`. Tests drive turns through `fixtures/claude-stub.ps1`, a fake `claude` that asks the Messages
stub for each scripted reply and runs each tool use through the real MCP server. Proven locally:
- Windows: .NET 688 unit, 70 integration (Cli 97.0%, Server 86.1%, Runner 88.0%, Store 97.3%, Core 99.7%); sixteen deliberate breaks, one per rule, each turned a test red.
  `idea-turn.Tests.ps1` 24, `serve.Tests.ps1` 43 on each implementation; Pester `-Tag Ported` 496 of 498 local and
  300 azure, and `-Impl dotnet` 438 of 439 local and 235 azure: the one test failing both ways (an idea turn must say a
  lease is held "on another machine") was fixed and passes, and the other was a TestDrive cleanup failure after a passing
  watchdog test (accepted risk). Playwright 41 on .NET and 41 on PowerShell.
- Linux (WSL): `-Tag Ported` 498 local, 300 azure (+2 skipped) both ways, clean; `idea-turn.Tests.ps1` 24.
- Live (todoapp-two, deploys off, the .NET dashboard and runner on the real state): local mode, session S-016 (5 turns on
  the subscription, $2.30) to J-035 (`docs/CONFIGURATION.md`, $2.07, PR todoapp-two #16); cloud mode, S-017 (4 turns on
  Foundry `fdy-factory-gc6c2b`, keyless, $1.29) to J-036 (`docs/TROUBLESHOOTING.md`, $1.59, `provider: foundry`, PR
  todoapp-two #15). Foundry's own estimate for the cloud session and job: $2.00 over 71 requests. Both jobs green on all
  gate checks with four PASSes; evals recorded (both good). J-035's first build was killed when the operator stopped the
  dashboard with a process-tree kill (its runner is a descendant on Windows); it was escalated and reworked.

## Goal

Idea sessions (grill, brief, specs) run on `ILlmProvider` like jobs do, and stop using
`ANTHROPIC_API_KEY` locally.

## Scope

- **One path for both modes** (Kurt, 2026-11-12, over the two paths first specced): every turn is a
  headless `claude -p` pass with the run mode's child environment, exactly as a job's: the
  subscription locally, Foundry in cloud mode. Later turns `--resume` the turn's claude session. The
  hand-written Messages-API loop, its Entra token code and the API key retire.
- **The idea tools** (`read_file`, `list_dir`, `grep`, `submit_specs`, `revise_spec`, `ask_questions`,
  `propose_scaffold`, `submit_brief`) are a stdio MCP server, `factory idea-mcp <S>`, and the only tools
  `claude` has (`--tools ""`, `--strict-mcp-config`). A command of the CLI rather than a `Factory.Mcp`
  project, so cutover keeps one binary.
- **The turn is ported**: `factory idea-turn <S>`. `idea-turn.ps1` forwards to it under every
  `FACTORY_IMPL`; there is no PowerShell turn left to keep in step.
- The session files under `sessions/<S>/` keep the contract's layout, so the dashboard is
  unchanged.
- Contract first: "Idea sessions" changes from "Claude API (key in `.env`)" to "follows the run
  mode". CLAUDE.md and the docs drop the API key.

## Tests

`idea-turn.Tests.ps1` runs against a fake `claude` (`FACTORY_IDEA_CLAUDE_CMD`) that replays scripted
model replies and calls the real MCP server for each tool use, so the tools run end to end. xUnit
covers the turn, the prompt, the stream parsing and each tool's rules. One live idea session per mode
ends in an approved spec.

## Done when

`.env` no longer needs `ANTHROPIC_API_KEY` for anything.
