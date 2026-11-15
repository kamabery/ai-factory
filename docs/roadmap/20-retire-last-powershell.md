# 20: Retire the last PowerShell

**Status:** done on `feature/20-last-powershell` (2026-11-15), PR to `main`. Decided: shell-neutral gates with
`factory check file`; `approve` refuses a shell line naming its rewrite, and approved jobs finish on `FACTORY_GATE_SHELL`;
`Factory.TestGates` and `Factory.Simulator` projects (not packed); the overrides name a program (TestHost's `tool`,
`signin`, `claude`, `env`, `launch` fakes). Windows: .NET 1054 unit, 196 integration + 1 skipped, gates green;
Playwright smoke 5/5, whole 41/41; Pester (spec 16's scripts) 29. WSL with no pwsh (~/.pwsh moved aside, no Windows
PATH): 1051 unit + 3 skipped, 193 integration + 4 skipped, gates green. Live proof: J-001 "Greet by name" on the new
hello target `kamabery/ai-factory-hello` (D5, private) ran end to end in WSL with no PowerShell, deploys off: gate
build/test/`factory check` green, four PASS, APPROVE, PR ai-factory-hello#1, $1.09, eval good. Found on the way: run.sh
copied the real `.env` into the WSL sandbox (now excluded), and the idea turn's MCP server lacked the turn's `.env`
(now passed). Left for later: the devops agent tries `aspire-addon` on a non-Aspire target, the lesson count counts
"None" gaps, and a signed-out claude in WSL costs a round (the eval's factory lessons). `grep pwsh` outside spec 16's
files finds only spec 16's Pester plumbing in the Linux scripts, the guard tests, and the legacy-shell fallback.
**Branch:** `feature/20-last-powershell`. **Depends on:** 12.

## Goal

The factory needs no PowerShell to run a job, to be tested, or to be developed. Spec 12 retired the control plane
and Pester, but left PowerShell in five places: the gate's shell, the simulator, the .NET suite's runner, the tests'
fake CLIs, and one repo skill. This spec removes all five. Spec 16 removes the setup scripts, which are the last
PowerShell left after this spec.

## Scope

1. **Gates without PowerShell (contract first).** Today every spec's `## Gate` is written in PowerShell and run with
   `pwsh -NoProfile -Command` (or `FACTORY_GATE_SHELL`, spec 12). Decide between the two designs below in the
   contract, then change `GateShell`, the spec-writer's instructions, `factory/templates/spec.md`, the bootstrap
   spec's gate lines (`BootstrapSpec`, `factory/templates/bootstrap.md`) and the grill/idea prompts:
   - **Shell-neutral gates (recommended):** a gate line is a program and its arguments, run with no shell
     (`dotnet test`, `npm --prefix src/ui/react run build`, `terraform -chdir=IaC validate`). The checks specs write in
     PowerShell today ("the file exists, has these headings, mentions these words") become a `factory check`
     command: `factory check file <path> [--heading <re>]... [--contains <text>]... [--matches <re>]...`, so they read
     the same on every OS. A gate needing a sequence or a condition, such as `npm ci` only when the lock file changed,
     is either a `factory check` option or a script the target commits.
   - **A gate language per spec:** `## Gate (sh)` or `## Gate (pwsh)`, with sh the default off Windows. This is
     simpler to build, but it ties a spec to an OS and keeps PowerShell for the specs that choose it.
   A spec written before this change (`## Gate` lines in PowerShell, as on the board and in the evals) is refused
   by `approve` with a message saying how to rewrite it. Specs already approved or building keep running on
   `FACTORY_GATE_SHELL` until they finish, and the contract says so.
2. **The simulator moves to .NET.** `simulate.ps1` becomes `factory simulate [--speed <x>] [--reset] [--state-dir <dir>]`,
   in the CLI or in a `Factory.Simulator` project. It runs on the same board through the CLI's commands and starts
   Factory.TestHost's GitHub stub when no env file names one. Playwright's `beforeAll` calls it. Its Pester
   case (`simulate.Tests.ps1`, the `-Reset` that waits out a held state dir) is pushed down to an integration test.
3. **The test fakes become TestHost modes.** `claude-stub.ps1`, `tool-stub.ps1` and `signin-stub.ps1` become
   `Factory.TestHost` modes (`claude`, `tool`, `signin`). The overrides `FACTORY_GH_CMD`, `FACTORY_AZ_CMD`,
   `FACTORY_SIGNIN_CMD`, `FACTORY_CANARY_SCAFFOLD_CMD`, `FACTORY_IDEA_CLAUDE_CMD` and `FACTORY_CLAUDE_CMD` name a
   program and its leading arguments (a JSON array, or a path for one program), not a script for `pwsh`. The .NET
   tests' pwsh stand-ins (`RunnerTests`'s fake claude, `FactoryLauncherTests`' parent, the `GateShellTests` cases
   written in PowerShell) use TestHost modes or `dotnet` instead. `ToolRunner.Pwsh` goes.
4. **`src/factory/test.ps1` becomes .NET.** It runs the tiers, prints the counts, merges the coverage and applies the
   gates. Either it becomes a small console project (`dotnet run --project src/factory/Factory.TestGates`), or it uses
   `dotnet test` with the coverage gates in MSBuild. The linux runner (`factory/tests/linux/run.sh`) calls it and
   `setup.sh` stops installing pwsh, unless spec 16 has not yet removed the setup scripts' Pester.
5. **`.claude/skills/ideate`** starts its window without PowerShell (`factory idea` in a new terminal, per OS).
6. **Docs:** CLAUDE.md, README (PowerShell drops out of the prerequisites), TESTING.md, PESTER-SUITE.md (removed
   once 16 lands), DEPLOY.md where it says the gate needs pwsh, and the contract's Gate, "Implementations" and test
   overrides.

Out of scope: `setup-azure.ps1`, `setup-platform.ps1`, `env-file.ps1` and their Pester (spec 16), and the template
workspace's own PowerShell (`template-projects`, a separate repo: its lessons go through the dashboard's Lessons tab).

## Tests

Unit-first, as in every port (TESTING.md). `factory check` gets unit tests for each option and its exact refusal
text; `GateShell` gets an integration test that runs a gate line with no shell on every OS. The TestHost modes get
integration tests through the overrides they stand in for. The Playwright suite runs against `factory simulate`.
Each rule is seen red by a deliberate break, and the PR carries a short mapping for `simulate.Tests.ps1` and the
PowerShell fakes, like spec 12's.

## Done when

- `grep -r pwsh` and `*.ps1` across `src/`, `factory/` (apart from the files spec 16 owns) and `.claude/` find
  nothing.
- `dotnet`-only test runs are green with the coverage gates, on Windows and in WSL with no pwsh installed; the whole
  Playwright suite passes.
- A real job whose spec uses the new gates runs end to end in WSL with no PowerShell installed, deploys off, and its
  eval is recorded.

After this spec and 16, the repo has no PowerShell.
