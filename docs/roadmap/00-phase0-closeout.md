# 00: Phase 0 close-out

**Status:** done (2026-11-09). The five in-repo items merged in PR #3 (CI green on its head). template-projects: `terraform init` retry merged in its PR #6, proven by canary 30 (factory-canary run 42653672964 ran the new step). todoapp-two: the `/health` docs ran as factory job J-032, merged as its PR #12, eval recorded (good). **Branch:** `feature/00-phase0-closeout` (deleted).

**Why:** the port reproduces the PowerShell behaviour. These follow-ups from PR #1
(`docs/PLAN-PR1-PROOFS.md`, Results) change that behaviour, so they land first and the oracle
holds still afterwards.

## In this repo

1. **`First-Error` catches "Error while installing".** A provider-install failure in an HCP log is
   reported as the first error instead of falling through. Pester: a fixture log with that line.
2. **The approver gets `deploy-log`.** Add it to the approver's allowed commands and prompt, so it can
   quote a failed attempt. Pester or a hook test proves the permission; the agent prompt names it.
3. **A usage limit is not `escalated`.** When `claude -p` stops on a subscription or Foundry usage
   limit, the job gets its own flag (proposed `usage-limit`) and is resumable, not escalated. Contract
   first (flag list, runner), then `claude-run.ps1` / `runner.ps1`. Pester through `tool-stub.ps1`.
4. **The devops agent may run `terraform -chdir=<dir> …`** in author mode. Today the permission rule
   denies it. Fix the allow rule in the plugin settings; test with the hook fixture.
5. **CI for the suites as they stand:** a GitHub Actions workflow on `windows-latest` that runs
   `Invoke-Pester factory/tests` and the Playwright check against `simulate.ps1`. The azure backend
   run uses `blob-stub.ps1`, so it needs no cloud. This is the baseline 01 widens to three OSes.

## In other repos (not this branch)

- **template-projects:** retry `terraform init` on provider-registry timeouts, in the deploy
  workflow. A template change; prove it with `factory.ps1 canary -Full`.
- **todoapp-two:** the `/health` docs are stale (it is `/api/health`, Development-only `/health`).
  Run it as a factory job; it needs no human session.

## Housekeeping

- Delete the merged local branches `feature/foundry-mode`, `feature/openrouter-azure`, `ideate-api`
  and the remote `feature/foundry-mode` (ask first).

## Done when

All five in-repo items are green in CI, the counts are in the commit messages, and the PR is merged.
