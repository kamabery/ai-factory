# ai-factory

This repo is **the factory**, a Claude Code software factory, as in the ZazenCodes
"Build Your Own AI Software Factory with Claude Code" video. It builds GitHub target repos, and new
targets are scaffolded from `D:\code\template-projects`. A `/factory` skill orchestrates
spec, build, four parallel reviewers and an approver; ideas are grilled and specced in a
dashboard idea session on the Claude API (key in `.env`), or in the CLI fallback, an interactive
`/factory:idea` session. The run mode picks the LLM: `local` (the default) runs jobs on the
subscription, and `cloud` (`FACTORY_MODE=cloud`) runs jobs and idea turns on Claude in Microsoft
Foundry, keyless through Entra ID (`docs/FOUNDRY.md`). Nothing picks a provider per job. Each job's branch is deployed to an ephemeral Azure preview
through HCP Terraform before the approver (`docs/DEPLOY.md`), and jobs that go bad leave lessons
the dashboard proposes as template fixes. A deterministic PowerShell control plane
owns state, the gate and the merge. A local dashboard is where the human works the factory:
backlog specs edited and approved by drag and drop, and finished work reviewed, merged and cleared.

The design rationale is in `D:\code\main-vault\ai-engineering\software-factories-agentic-os-and-harnesses.md`
and `...\build-your-own-ai-software-factory-with-claude-code.md`. How to build the rest of it is in
`docs/BUILD-GUIDE.md`.

## Factory rules

- **`docs/FACTORY-CONTRACT.md` is the source of truth** for stages, transitions, file layout,
  `board.json`, `events.jsonl` and verdict headers. Change it first, then the code.
- **Every state change goes through `factory/scripts/factory.ps1`.** Never edit
  `.factory-runs/board.json` or `events.jsonl` by hand or with a file tool, not even to "fix" one.
  If the script refuses a transition, that is the factory working; escalate rather than route around it.
- **`.factory-runs/` is gitignored on purpose.** Factory state must never dirty the release checkout.
  In the source video, a modified `backlog.md` blocked an approved merge. Don't move state into
  tracked files.
- **Approval is not merge.** The approver's `APPROVE` only moves a job to `gate`, and a green gate
  only moves it to Ready for review. Only a human accepts: a drag to Done runs `factory.ps1 merge`,
  which still requires a green gate and a clean tree, or the human merges the PR on GitHub, which
  `sync-prs` records as Done (an ungated head is noted, not refused).
- **The dashboard's write endpoints are thin `factory.ps1` wrappers.** `serve.ps1` holds no
  transition logic of its own.
- **Targets are GitHub repos (`owner/name`), and every job builds in its own temp clone**,
  `<work>\jobs\<id>` (`$env:FACTORY_WORK`, else `%TEMP%\ai-factory`), one job at a time. The
  clone is deleted when the job reaches Done (its PR merged) or is discarded. Only `factory.ps1`
  holds the GitHub token (`.env`) and pushes; agents never push.
- **No agent weakens the thing that judges it.** Factory jobs never edit `factory/`, `.claude/`,
  `docs/FACTORY-CONTRACT.md` or test assertions in order to pass. Changes to the factory itself are
  made by a human session, not by `/factory`.
- **Previews are never production.** The deploy stage applies only `j<NNN>` previews; dev and
  staging are ready to deploy by hand, and nothing in the factory deploys prod.
- **Tests are seen red before green**, and follow `docs/TESTING.md`: unit-first .NET (DI, NSubstitute,
  AutoFixture, coverage gates), Pester as the port's oracle, and Playwright as a smoke set with the
  whole suite on demand. **Tests run locally before a PR, never in CI**: the repo has no test
  workflows (Actions cost), and the PR description carries the local counts. The factory scripts are tested with Pester
  (`Invoke-Pester factory/tests`) and the dashboard with a Playwright check against `simulate.ps1`.

## Commands

```powershell
pwsh factory/scripts/serve.ps1               # dashboard on a random localhost port it prints; -Port N to fix one
pwsh factory/scripts/idea.ps1 <owner/name|I-NNN> # CLI fallback for the dashboard's New idea: grill -> brief -> specs -> you approve
pwsh factory/scripts/runner.ps1              # drains the Approved queue; serve.ps1 starts it for you (after step 2)
Invoke-Pester factory/tests                  # control-plane, runner, idea-turn and serve tests (after steps 1-2)
pwsh factory/tests/run-all.ps1               # the same plus the azure backend run, one process per Pester block in parallel
pwsh factory/tests/run-all.ps1 -Tag Ported -NoAzure   # the ported commands' Pester blocks, on PowerShell and on .NET (FACTORY_IMPL)
pwsh src/factory/test.ps1                    # the .NET port: unit + integration tests, tier counts, coverage gates
cd factory/tests/dashboard; npx playwright test --grep @smoke   # the dashboard smoke set; drop --grep for the whole suite
$env:FACTORY_TEST_STATE='azure'; Invoke-Pester factory/tests/factory.Tests.ps1   # the same on the azure state backend (blob stub)
pwsh factory/scripts/factory.ps1 state-migrate | allow-ip | prune   # FACTORY_STATE=azure: state in Blob storage (docs/STATE.md)
pwsh factory/scripts/setup-platform.ps1                # the shared platform previews run on, ~$18/month (docs/DEPLOY.md)
pwsh factory/scripts/factory.ps1 canary -Full          # after template IaC changes: the whole stack, off the platform
cd factory/infra/llm; terraform test; terraform apply  # the Foundry account cloud mode runs on (docs/FOUNDRY.md)
pwsh factory/scripts/setup-azure.ps1 -SubscriptionId <id> -HcpOrg <org> [-CreateExternalTenant <sub> -ExternalResourceGroup <rg> | -ExternalTenantId <id>]  # one-time preview identities (docs/DEPLOY.md)
```
