# The Pester suite

`factory/tests/*.Tests.ps1` tests the factory's PowerShell control plane: the scripts that own
state, transitions, the gate, merge and deploy. It runs entirely offline. No GitHub PAT, `claude`
CLI, HCP Terraform or Azure is touched, and nothing in `.factory-runs/` is read or written.

```powershell
Invoke-Pester factory/tests                                           # everything (~10 minutes)
Invoke-Pester factory/tests/factory.Tests.ps1                         # one file
Invoke-Pester factory/tests -FullNameFilter '*bootstrap*' -Output Detailed   # by test name
```

Requires PowerShell 7 and Pester 6 (each file has `#Requires -Modules Pester 6.0.0`). The last
full run was 221 tests, all passing.

The dashboard is not covered here; it has its own Playwright check against `simulate.ps1`
(`factory/tests/dashboard`, see the README).

## What each file covers

| File | Script under test | Covers |
| --- | --- | --- |
| `factory.Tests.ps1` | `factory.ps1` | The control plane, against [FACTORY-CONTRACT.md](FACTORY-CONTRACT.md): ideas and specs, the run modes provider on cost events, board writes, illegal transitions, the Approved queue, review routing, the approver's decision, the gate, publish and merge, human moves from Ready for review, purge, bookkeeping (cost), evaluations, targets and bootstrap jobs, deploy / redeploy / destroy, and lessons |
| `runner.Tests.ps1` | `runner.ps1`, `hook-event.ps1`, `serve.ps1` | The runner draining the queue with a fake `claude`, its log and resilience, the run modes child environment (subscription and Foundry), state resolution, `serve.ps1`'s runner watchdog and PR sync, and the hook that turns agent start and stop into events |
| `serve.Tests.ps1` | `serve.ps1` | The dashboard's HTTP endpoints: idea sessions on a new and an existing target, the repo list and Add new, evaluations, lessons, previews, state files, and the run mode in the config |
| `idea-turn.Tests.ps1` | `idea-turn.ps1` (the .NET CLI's `idea-turn`) | One idea-session turn as a `claude -p` pass: its arguments, prompt on stdin, model, run-mode environment (no key locally, Foundry in cloud mode) and system prompt; the idea tools through the real MCP server (the fence, specs, UI tools ending the turn); resuming the claude session with the human's answer; cost; errors, the turn lock and a refusal |
| `setup-azure.Tests.ps1` | `setup-azure.ps1` | The one-time preview identities in [DEPLOY.md](DEPLOY.md): app registrations, service principals, role assignments, External ID, and reruns against what already exists |

## How it stays offline

Each test gets its own state directory under Pester's `$TestDrive`, through `FACTORY_HOME`, and
`FACTORY_ENV_FILE` points the scripts at a test `.env` in place of the repo's. That file holds
throwaway credentials and the stubs' URLs (`GITHUB_API_URL`, `HCP_API_URL`, `ANTHROPIC_BASE_URL`).
Every other external dependency is swapped through a variable the scripts already read:

| Stands in for | How | Fixture |
| --- | --- | --- |
| GitHub REST API, GitHub Actions and the HCP Terraform API | one local HTTP stub at `GITHUB_API_URL` and `HCP_API_URL`; targets are bare git repos (`FACTORY_GIT_BASE`) cloned over `file://` | `fixtures/github-stub.ps1`, started by `fixtures/github.ps1` |
| Claude Messages API (streaming) | a local stub that replays scripted responses and saves each request; idea turns reach it through the fake `claude` below | `fixtures/anthropic-stub.ps1` |
| The `claude` CLI in an idea turn | `FACTORY_IDEA_CLAUDE_CMD`: takes `claude`'s arguments and stdin, starts the real `factory idea-mcp` server, asks the Messages stub (`FACTORY_IDEA_STUB_URL`) for each reply, runs each tool use through MCP, and prints `stream-json`; it records what it was given in `FACTORY_IDEA_STUB_STATE/last-run.json` | `fixtures/claude-stub.ps1` |
| `gh` and `az` | `FACTORY_GH_CMD` and `FACTORY_AZ_CMD`; each call is appended to `FACTORY_TOOL_LOG` for the test to assert on | `fixtures/tool-stub.ps1`, `fixtures/az-setup-stub.ps1` |
| The `claude` CLI | `FACTORY_CLAUDE_CMD`: a fake that commits on the branch and drives the job through `factory.ps1` | inline in `runner.Tests.ps1` |
| Gate commands | `FACTORY_GATE_CMDS`: JSON list of `{ name, cmd }` with chosen exit codes | inline in `factory.Tests.ps1` |
| Hook payloads | sample `SubagentStart` / `SubagentStop` JSON | `fixtures/subagent-*.json`, `fixtures/internal-stop.json` |
| Polling delays | `FACTORY_DEPLOY_POLL_SECONDS`, `FACTORY_SETUP_POLL_SECONDS` | — |

Within a stub, failure modes are switched by data rather than by mocks. For example, a
`deploy-fail.txt` on the branch makes the dispatched Actions run fail, and a write the stubbed GitHub
PAT lacks permission for returns 403 with `X-Accepted-GitHub-Permissions`. The header of
`github-stub.ps1` lists every route it serves.

## Conventions

- **The contract comes first, then a failing test, then the code.** A change to the factory updates
  [FACTORY-CONTRACT.md](FACTORY-CONTRACT.md), adds or tightens a test, sees it fail, then makes it
  pass.
- **Tests run the real scripts** (`& factory.ps1 ...`) and assert on `board.json`, `events.jsonl`,
  `$LASTEXITCODE` and the stubs' request logs. Nothing in the scripts is mocked.
- **Seeding state by writing `board.json` is allowed only in tests**, inside `$TestDrive`, to set up a
  situation that would be slow to reach through the commands, such as a job already on its third
  deploy attempt. The real `.factory-runs/` is still only ever changed through `factory.ps1`.
- **Factory jobs never edit these tests.** See "No agent weakens the thing that judges it" in
  [CLAUDE.md](../CLAUDE.md).
