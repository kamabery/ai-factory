# Plan: prove and fix what PR #1 left open

PR #1 (`feature/deploy-stage`) lists two things as not proven and four as known issues. This plan
closes all six on the same branch, and the PR merges after the proofs pass. Decided with Kurt on
2026-11-07. Claude drives it; merges stay with Kurt.

## What the code showed

- **j026 probably never passed a `/health` check.** `Invoke-Deploy` doesn't call `Invoke-Smoke`, and
  the only 14:19 evidence is the sign-in log `smoke-1.log`. The J-026 eval and the PR are wrong on
  this.
  - `todoapp-two` maps `/health` only in Development (`Extensions.cs:118-121`), which would explain
    the 404.
  - The 500 came about 12 idle minutes after the deploy, with `api_min_replicas = 0`. A cold start
    through the Static Web App is a guess, not a finding.
- **Adding step 7 fails every `todoapp-two` deploy** until that mapping is fixed. Phase B uses this.
- **`todoapp-two` can't prove isolation.** Its IaC has no `use_platform`, so it builds a full stack.
- **A deploy-fix can stop as `out-of-scope`.** Only `rework` widens a spec's scope. Kept on purpose:
  no agent widens what judges it.
- **`First-Error`, over 32 real logs:** one wrong line (canary 21), six useless first lines
  (canaries 6, 16 and 24, and J-019's three attempts).
- **The Playwright flake reproduced:** 1 of 30 runs, in `beforeAll`, when `simulate.ps1 -Reset`
  exited non-zero. The error text wasn't captured, so the cause is still unknown.

## Phase A: fixes, no cloud

Each is seen red, then green.

1. **`First-Error`.** Stop matching the word "error" anywhere; use the failing step's last output
   above the exit line; add Terraform's detail to a generic `Error:` summary. Test cases are excerpts
   of the real logs. Done when all 32 logs read correctly.
2. **Step 7.** `Invoke-Deploy` runs `Invoke-Smoke` before the sign-in smoke. A failure is a failed
   attempt, and `deploy-<a>.log` ends with `## Smoke`.
3. **Playwright.** Capture the setup's stderr and stress until it recurs, then fix what it shows,
   with a deterministic red test if it is a lock. Done after 100 clean repeats. If it won't recur in
   200 runs, the PR says the cause is still unknown. No Playwright `retries`.
4. **Correct the record:** the J-026 eval and the PR text.
5. **Canary:** `deploy.ps1` changes the fingerprint, so one must pass before phase B.

## Phase B: a real job on `todoapp-two`

A small unrelated ticket whose spec doesn't mention `/health`. Nothing is planted.

| Claim | Passes when | Evidence |
|---|---|---|
| Shell limit | The first `deploy` call runs past 10 minutes and returns with no resume | The run's transcript |
| Step 7 | The 404 fails the attempt with a `## Smoke` section | `deploy-1.log` |
| `deploy-fix` | The agent files it, and the job returns to build as a new round | `events.jsonl`, `deploy-fix-<n>.md` |
| Scope stop | The fix in `servicedefaults` stops as `out-of-scope`, and a rework with `Scope +` clears it | `events.jsonl` |
| The fix | A later round's preview answers 200 at `/api/health` and the job reaches Ready | `deploy-<a>.log`, board |

The shell limit is proven to about 30 minutes, not 90. The PR says so.

**If the agent chooses `block`:** read its reasoning. If the call was fair, it counts as a second
block proof. A human session then fixes `/health`, and deploy-fix is proven on `factory-proof` with a
migration that fails only on Postgres, such as `CREATE INDEX CONCURRENTLY` inside a transaction.

## Phase C: a throwaway on-platform target

A new private repo, `kamabery/factory-proof`, scaffolded like the canary (Postgres, React, auth on),
with a bootstrap job and two small feature jobs. Confirm first that bootstrap scaffolds from the local
`template-projects` checkout, so template PR #1 needn't merge first.

**`block` and `redeploy`,** on the first feature job:
1. `az postgres flexible-server stop` on the shared server, with no other preview relying on it.
2. The deploy fails. Pass: the agent runs `block` with no IaC commits, and the card shows `environment`.
3. Start the server, then `redeploy`. Pass: the preview comes up and the job moves on.

**Isolation,** with both feature jobs' previews up:
- Neither preview's Postgres role can reach the other's database.
- Each identity's vault role assignments cover only its own secrets.
- Data written to one preview doesn't appear in the other.
- Destroying one while the other redeploys leaves the survivor answering 200, with its role, database
  and secrets intact.
- Nothing of the destroyed preview is left on the platform.

Only one job runs at a time, so two simultaneous applies aren't tested. The PR says so.

## Phase D: the health probe

On the previews from B and C, while each is warm and after each has idled past scale-to-zero: GET
`/api/health` through the Static Web App, and record the status and `Server` header.

- A 404 on the old `todoapp-two` mapping and 200 on the template confirms the mapping.
- If the cold 500 is confirmed, keep scale-to-zero, make the approver's preview check retry, and note
  it in `docs/DEPLOY.md`.

## Finish

- An eval for every job (`factory.ps1 eval`), including failures.
- Update PR #1: each claim with its result, what wasn't exercised, and the suite counts.
- Kurt merges PR #1. Deleting `factory-proof` is his call.

**Estimate:** about $20 in agent runs and most of a day, extrapolated from J-026's $3.25. The platform
already costs about $18 a month.
