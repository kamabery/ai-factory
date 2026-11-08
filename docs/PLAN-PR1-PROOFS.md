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

## Results (2026-11-07 to 2026-11-08)

| Claim | Result | Evidence |
|---|---|---|
| `First-Error` | Fixed. All 32 old logs read correctly; live, it named `AADB2C` (J-027) and `25001` (J-029) | `98097b4`; events |
| Step 7 HTTP smoke | Fixed and live: caught the 404 twice, then answered 200 | J-027 `deploy-1.log`, `smoke-1.log` |
| Playwright flake | `simulate.ps1 -Reset` raced a live serve. 4 of 90 failed before the fix, 100 of 100 passed after | `1253865` |
| Shell limit | Proven to about 30 minutes, not 90: one `deploy` call ran 32 min 47 s and returned | J-027 `run-1.jsonl` |
| Infrastructure fix path | Proven: the agent fixed the Entra race in IaC, and the next attempt passed it | J-027 `4918912` |
| `block` | Proven twice: J-027's `/health` 404, and J-029's GitHub outage. Both fair calls with no commit | flags `environment` |
| `deploy-fix` | Proven, with the fault planted by a human rework: `25001`, round 3 with the diagnosis, re-review, deploy 200, APPROVE | J-029 `deploy-fix-3.md` |
| Scope stop | Not exercised live; the Pester stubs only | |
| j026's 500 and 404 | Reproduced: a cold start answers 500 `Backend call failure` after 45.6 s, then 404 because `/health` was Development-only | J-027 preview |
| Isolation | Each role is refused CONNECT on the other's database and can't assume its role. Each identity reads only its own secret. Destroying J-030 left J-029 healthy and the platform clean | `factory/scripts/isolation-check.ps1`, run by Kurt |

**Not covered:** two simultaneous applies (one job runs at a time), the data-row check (the script used a wrong
column, `UpdatedAt` for `UpdatedOn`), and the full 90-minute limit.

**Changed on the way:**
- The agents' preview checks retry and go through the front end (`590faf6`, `5eaae6a`).
- `todoapp-two` maps `/health` in every environment (J-027 `866ab95`).
- The template pack was rebuilt to pick up `5c4fa6e`. Canary 28 passed with it, the role-race fix's first run on Azure.

**Follow-ups:** `First-Error` on "Error while installing"; give the approver `deploy-log`; retry `terraform init`
on provider timeouts (template); the stale `/health` docs in todoapp-two; a usage limit reads as `escalated`; the
devops agent's `terraform -chdir` is denied in author mode.

## Finish

- An eval for every job (`factory.ps1 eval`), including failures.
- Update PR #1: each claim with its result, what wasn't exercised, and the suite counts.
- Kurt merges PR #1. Deleting `factory-proof` is his call.

**Estimate:** about $20 in agent runs and most of a day, extrapolated from J-026's $3.25. The platform
already costs about $18 a month.
