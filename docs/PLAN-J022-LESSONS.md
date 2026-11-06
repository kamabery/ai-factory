# Plan: the J-022 lessons

J-022 ("Sign-in that works under aspire run and in Azure") merged at round 11 for $27.09, against a
$7.29 sibling. The lessons-learned session of 2026-11-01 decided six factory changes. Their eval is
`.factory-runs/evals/J-022.md`, and its template lessons are in `.factory-runs/lessons/J-022.md`.

**The contract is already written** (`docs/FACTORY-CONTRACT.md`, uncommitted on
`feature/deploy-stage`). This plan is the code that makes it true. Read these contract sections first:
- Targets: the Scope: In glob format
- Job transitions: the new `build → ready/blocked` rows and `rework -Tweak`
- Blocked: the factory-fault paragraph, **Build checks** and **Tweak rounds**
- Lessons: build.md template gaps
- `board.json`: `roundStart`, `tweak`, `baseMoved`, `skipped`
- `factory.ps1` commands: `gate-check`, `provenance`, `tweak-check`
- Agents
- Gate and merge: `gate-check`
- Deploy: step 7a, the sign-in smoke
- Dashboard API: the rework `tweak`
- Runner: `roundStart`

## Rules for every phase

- Each phase is red then green: write the Pester (or Playwright) test, see it fail, then implement.
  Run `Invoke-Pester factory/tests`, plus the azure backend run
  (`$env:FACTORY_TEST_STATE='azure'; Invoke-Pester factory/tests/factory.Tests.ps1`) for anything
  that writes state.
- A human session changes the factory, never `/factory`.
- `serve.ps1` stays a thin wrapper and holds no logic.
- Commit per phase, with the Pester count in the message, as the recent commits do.

## Phase 1 — `factory.ps1 gate-check` (closes the allowlist gap named by J-017, J-019 and J-022)

The smallest change, and the one that would have prevented round 11.

1. Tests (`factory.Tests.ps1`), using `FACTORY_GATE_CMDS` for the commands:
   - It runs every command, or the one named, in the work dir, prints the output, and exits with the
     first non-zero code.
   - Board `gate`, stage, flag and `events.jsonl` are unchanged afterwards.
   - It refuses a job in `backlog`, `approved`, `ready` or `done`.
   - A second call on the same head sha returns the cached result with `(cached at <sha>)` and does
     not re-run (prove it with a command that appends to a file).
   - A dirty tree is not cached.
   - The lock: two concurrent calls (Start-Job) run one after the other, never overlapping. Prove it
     with timestamps written by the command.
2. Implement it in `factory.ps1`. Share the command parsing (including the dotted `-name=value`
   quoting) with `gate` through one function; don't copy it.
3. Prompts: `factory/plugin/agents/builder.md`, the four reviewer agents and `approver.md` must use
   `F gate-check <id> [<name>]` for gate commands. The builder must not hand on with a red or unrun
   gate command. Add `gate-check` to the commands table in `SKILL.md` if it lists them.
4. Keep the `terraform …` allow rules in `claude-run.ps1`. Agents may still run ad hoc
   `terraform fmt` and `validate`.
5. **Verify:** a simulator run (`simulate.ps1`) or a live job where a reviewer's verdict quotes a
   `gate-check` result for iac-test. **Done 2026-11-01, J-023:** the builder saw `caps_log_ingestion`
   red then green through `gate-check`, and the code, security and ux reviewers quote
   `iac-test exit 0 (cached at dc533a4)`, 11 passed. No agent hit "requires approval".

## Phase 1b — the `blocked` stage (found missing while starting Phase 2)

The contract has described `blocked` since fa212ec, but no code had it: every escalation landed in
`ready`. Phases 2 and 3 send a deployable job there, so it is built first. Done:
- `factory.ps1`: an escalation (`set-stage … -Reason escalated`, `max-rounds`, `deploy-failed`,
  `github-permission`) parks a deployable job in build, review or deploy in `blocked`; anything
  else still lands in `ready`. `rework`, `discard` and `publish` take a blocked job, `redeploy` takes
  one blocked from `deploy` only, and `merge` refuses it. It is not building, so it does not hold the queue.
- `runner.ps1` publishes a blocked job and compiles its lessons, as for `ready`.
- `serve.ps1`: `POST /api/jobs/<id>/redeploy`, and the rework move from `blocked`.
- Dashboard: blocked cards in Building with their flag, PR, **Retry deploy** (from deploy only),
  **Rework** and **Discard**, on the card and in the modal.

## Phase 1c — the rest of the deploy contract that is not built (not started; ask first)

Also in the contract since before this plan, and not in code:
- five deploy attempts a round (the code allows three),
- `factory.ps1 deploy-fix <id> -File <diagnosis>`, `deployFixes` and the `deploy-fix-rounds` flag,
- `factory.ps1 block <id> -Detail <line>` and the `environment` flag.

## Phase 2 — the scope check (it would have stopped round 3's `adopt.tf`)

1. Tests:
   - The parser turns `## Scope` / `- In:` backticked sub-bullets into globs, and ignores prose and
     inline backticks after the glob (J-022's `` `.aspire/apphost.cs` (Keycloak `#:package`) ``).
   - Glob matching: `*` stays within one segment, `**` crosses segments.
   - `set-stage review` with every path in scope moves to `review`. With one stray path it moves a
     non-deployable job to `ready`, and a deployable one to `blocked`, with `out-of-scope`, the
     detail and a `why` listing the paths. The diff is `factory/base...HEAD`, so merged base work
     is not counted (test: a base commit touching an out-of-scope file).
   - A spec with no In globs moves to `review` with a `note` event "not scope-checked".
   - `rework` with `Scope +: a/**, b.txt` appends the globs to `spec.md` as `(rework <n>)`
     sub-bullets and appends a note. The next `set-stage review` accepts those paths.
2. Implement it in `factory.ps1` (`set-stage` build→review, `rework`).
3. Update `factory/templates/spec.md` (the In sub-bullet format, with an example) and
   `factory/plugin/agents/spec-writer.md` (list generated outputs). Also update the idea-api prompt
   if it inlines the spec template; it is assembled from `templates/spec.md`, so probably not.
4. Dashboard: the flag text for `out-of-scope` in the job modal's reason map, and a Playwright check
   that a blocked `out-of-scope` card shows the paths.

## Phase 3 — base moved (it would have turned round 6 from a surprise into a decision)

**Done 2026-11-02.** `simulate.ps1` writes its own build.md, so it now writes `Spec holds: yes` for a
job with `baseMoved`. The chip shows on any card with `baseMoved`, not only Backlog, Approved and Ready.

1. Tests:
   - `merge` of J-A sets `baseMoved` on each open job of the same idea (backlog, approved, building,
     ready and blocked) and appends a note, but not on `done` or `discarded` jobs or other ideas'
     jobs. Repeat for `sync-prs` recording a GitHub merge.
   - `set-stage review` on a job with `baseMoved`:
     - with no `## Spec against base` section, it exits 2 and does not move;
     - with `Spec holds: yes`, it moves and clears `baseMoved`;
     - with `Spec holds: no`, it moves to `ready` or `blocked` with `spec-stale`, `why` = the
       section, and keeps `baseMoved`.
   - `rework` clears `baseMoved`.
2. Implement it in `factory.ps1`.
3. `factory/templates/build.md`: add the `## Spec against base` section ("only when the job has
   base moved"). `builder.md`: how to read the spec against the new main. The orchestrator must
   pass `baseMoved` to the builder; `F status -Json` already exposes the job.
4. Dashboard: the "base moved: J-NNN merged" chip on Backlog, Approved and Ready cards, with a
   Playwright check.

## Phase 4 — template provenance and lessons from build.md

**Done 2026-11-02.** The assumption held: J-018, todoapp-two's bootstrap, is still on the board with
`merged.sha` `8df0096`. The bootstrap is found by target, not by idea, since later jobs come from
other ideas. `roundStart` was in the contract but never recorded; `checkout` now records it on a
build round's first checkout, and every move into build clears it. A round's paths are its own
commits (`--no-merges ... --not factory/base`), so base work the builder merged in is not counted.

1. Tests:
   - `checkout` records `roundStart` (= HEAD after the checkout, before the build).
   - `provenance` lists a path changed since `roundStart` whose adding commit is reachable from
     the bootstrap job's `merged.sha`. It also lists one whose adding commit's subject is
     `addon: auth`. It does not list a path added by an ordinary commit. The bootstrap sha comes from
     the board, and from `evals/<id>.json` when the bootstrap job has been purged.
   - `set-stage review` in round > 1 exits 2 and prints the missing paths when build.md's
     `## Template gaps` does not name each one. It passes when every one is named as a gap or as
     `job-specific`.
   - `lesson <id>` compiles gaps from `round-*/build.md` as well as deploy.md, and skips
     `job-specific` lines and `None`.
2. Implement it in `factory.ps1`.
3. `factory/templates/build.md`: add a `## Template gaps` section. `builder.md`: run `F provenance`
   before handing on, and make every `dotnet new` or `aspire-addon add` its own commit with a
   `scaffold:` / `addon:` subject. `factory/templates/bootstrap.md`: the same commit rule.
4. **Assumption to check:** todoapp-two's bootstrap job may be purged and have no eval. Look on the
   board and in `evals/` for its `merged.sha`. If it's missing, the `addon:` subjects carry future
   jobs, and existing targets get no provenance for scaffold files. That is acceptable, but
   `provenance` should print a note about it.

## Phase 5 — the post-apply sign-in smoke (it would have caught AADSTS500011 before UAT)

**Done 2026-11-04: built (`ced8298`, `3fd3ee1`, `d314526`) and proven live on J-025's preview.**
- J-024 (2026-11-03) was the first job deploy to run the smoke. It passed and left nothing to show,
  so `d314526` keeps the steps in `jobs/<id>/signin/smoke-<a>.log` and a signed-in screenshot.
- J-025's deploy: smoke passed, log and screenshot present (QA j025 signed in, `/api/me` 200).
- Break tests on the j025 preview, each restored and read back:
  - Identifier URI cleared: the smoke still **passed**, correctly. Since J-022 the app requests
    `<client id>/access`, so sign-in no longer depends on the URI. This break cannot fail on a
    target that has J-022's fix.
  - SPA redirect URI removed: the smoke **failed** with `AADSTS500113: No reply address is
    registered`, with url, page text and screenshot. This is the live red.
  - Just after the restore it failed again (`AADSTS9002326`, cross-origin redemption needs the
    SPA client type) while Entra replicated; it passed about 90 s later. **Open, for Phase 7:**
    the smoke fails at once on any AADSTS, so replication lag right after a fresh apply could fail a
    good deploy. Consider a short retry on AADSTS in the first minutes, or a settle delay.
- Canary 18 (no-auth scaffold from `.env`) passed 2026-11-03; the template's auth add-on still needs
  canaries 15/16's fixes before the canary goes back to `auth: true`.

History: step 1 found the contract wrong:
the endpoint is `GET /api/me`, `[SignInRequired]`, so the smoke watches the app's own call (with its
token) rather than calling it. The script passed against a local fake IdP (ok, AADSTS, /api/me 401).
The live auth canary did not reach the smoke:
- Canary 15: a fresh scaffold has no `entra_external_*` ids (a target commits them). Fixed: the
  canary takes them from `.env` (`FACTORY_ENTRA_*`) and refuses without them.
- Canary 16: the template auth add-on's `msgraph_resource_action.user_flow_link` raced the new app
  registration ("The application id ... is invalid", AADB2C, 400 for 11 min). J-023's preview did
  the same link fine, and the template still lacks J-022's auth fixes. **Template lessons:** give
  `user_flow_link` a `depends_on` on the service principal and the identifier URI; apply J-022's
  auth lessons; the add-on's precondition and the deploy-app skill say "HCP project's variable set",
  but the ids belong in the target's tfvars (`docs/DEPLOY.md`).
- Both canaries' destroys failed too (15: the same Entra error; 16: the Container Apps environment's
  delete timed out). Leftovers were checked live each time.
- Kurt's call: `.env` sets `FACTORY_CANARY_SCAFFOLD` to the no-auth scaffold so deploys are unblocked
  (canary 17), and the smoke is proven on the next todoapp-two preview. The code default stays
  `auth: true`; remove the `.env` line once the template is fixed. The break test (clear a preview's
  identifier URI) is approved, on a preview only.
- Not built here, found on the way: job deploys never ran step 7's HTTP smoke; only the canary does.

1. **Verify first, before writing tests.** Read the live state, not the stubs, per the
   read-live-state rule:
   - Does the React auth seam expose `GET /api/auth/me`, or something else that answers 200 only
     when signed in? The contract assumes `/api/auth/me`. If it's something else, correct the
     contract first.
   - What the header shows when signed in (the selector the smoke waits for).
   - The Entra External ID sign-in page's form for a QA user: email page, password page, any
     "Stay signed in?" interstitial. Do this by hand once on a live preview, with the QA login
     from the dashboard.
   - That `qa_user` / `qa_password` are workspace outputs on a todoapp-two preview (they are what
     `qa-login` reads).
2. Tests (`factory.Tests.ps1` deploy section, with `FACTORY_SIGNIN_CMD` as a stub script):
   - The smoke runs only when the outputs have both QA values and there is a front end.
   - The password reaches the stub on stdin, and appears in neither the args nor `deploy-<a>.log`.
   - A stub exit 1 is a failed attempt: the `## Sign-in smoke` section is in the log, `status` is
     `failed`, and the job is not moved to `approve`.
   - A stub exit 0 lets step 8 proceed.
   - It also runs in the canary when the canary scaffold has `auth: true`.
3. Implement `factory/scripts/signin-smoke.mjs`. Reuse the Playwright already installed for
   `factory/tests/dashboard`, and avoid a second `node_modules` if possible. Then call it from
   deploy step 7a, in `deploy.ps1`, which the canary shares.
4. Consider switching the canary's default scaffold to `auth: true`, so the smoke is proven
   end to end at about $0.01. Ask Kurt; it changes the fingerprint and costs a canary run.
5. **Verify live:** a deploy of a todoapp-two job (or `canary -Force` with auth) whose log shows the
   smoke passing. Then deliberately break it (clear the identifier URI by hand on a preview) and
   watch the smoke fail. Kurt has to approve touching the live tenant.

## Phase 6 — tweak rounds (cuts turnaround on UAT polish such as round 7)

Last, because it is the largest change and the least certain gain.

**Measured 2026-11-04; Kurt chose the front-end-only deploy instead of the tweak path (deferred).**
Over 15 deploys (J-022 to J-025) the devops agent adds about 1 min. The GitHub run is the rest:
- Redeploy over a live preview: ~10 min. tf apply (registry/db) 1.1, images 2.4, db firewall
  open+close 2.1, schema 0.5, tf apply 2.2, publish front end 1.0.
- Fresh preview: ~31 min, with the two tf applies at 11.5 + 12.4. That part belongs to Phase 7.
- About $2 a round, ~75% Opus (orchestrator, builder). The four reviewers cost < $0.50.
The tweak path saves ~2-3 min and ~$0.30 per polish round. A front-end-only deploy (when only
`src/ui/**` changed: skip Terraform, images, firewall and schema; publish the front end) saves ~8 of
~16 min. It is a template change in `template-projects`, proven on a todoapp-two preview. Steps 2-4
below are deferred.

**Front-end-only deploy done 2026-11-05.**
- **Factory side (`5813a47`).** `factory.ps1 deploy` passes `since = deploy.sha` only when three
  things hold: the preview is `up`, the attempt before didn't fail, and the branch's `deploy.yml`
  declares `since`.
- **todoapp-two side (J-026, PR #9, merged as 1d5a34f).** The workflow decides the path with
  `.github/scripts/front-end-only.sh`. The gate tests that script with 23 cases.
- **Proven live on `j026`.** A dispatch with `since` set to the applied head took **1 min 23 s**,
  against ~10 min for a full redeploy. Both applies, the images, the firewall and the schema were
  skipped, and the sign-in smoke passed afterwards.
- **Not yet proven live:**
  - a non-empty `src/ui/` diff (the live case was an empty diff);
  - the factory sending `since` itself on a rework round.

  The next todoapp-two rework proves both.
- **Template port.** `.factory-runs/lessons/J-026.md` holds it, plus `.gitattributes` with
  `*.sh text eol=lf`. It waits for `template-projects`' `feature/hcp-ephemeral-deploy` branch to
  be sorted out.

1. **Measure first.** Get round 7's per-stage time from `events.jsonl` and its token cost from
   `run-11.jsonl` (`modelUsage`). Then split the deploy's ~10 min into the agent's operate pass and
   `factory.ps1 deploy` itself (the GitHub run and the HCP apply). If the apply dominates, skipping
   the devops agent saves little, and a front-end-only deploy (a `deploy.yml` input that skips
   Terraform when only `src/ui` changed) is the real win. That is a **template** change, so it
   becomes a lesson or a separate template job, not part of this phase.
2. Tests:
   - `rework -Tweak` sets `tweak`.
   - `tweak-check` prints `tweak` for a `src/ui/**` diff under the limits. It prints `full: <why>`
     and clears `tweak` for `IaC/x.tf`, for a path with `auth` in it, for 11 files, and for 301
     lines.
   - `set-stage review` in a tweak marks reviewers `skipped` per the path rule, and `read-reviews`
     counts `skipped` as PASS.
   - `tweak` is cleared when the round leaves `build`.
   - serve: `POST move { to: approved, note, tweak: true }` calls `rework -Tweak`.
3. `SKILL.md`: the tweak path (no devops author pass, `tweak-check` after the builder, only the
   reviewers not `skipped`, and `F deploy` directly with the devops loop as the fallback on
   failure).
4. Dashboard: the **Tweak** checkbox and the factory-fault hint in the rework dialog, the skipped
   reviewer chips, and a Playwright check against `simulate.ps1`.

## Phase 7 — deploy time (from J-023, added 2026-11-01)

J-023's fresh `j023` preview took 28 minutes to apply, against about 10 for a re-apply on existing
resources (J-022), and the devops agent's 10-minute shell limit escalated it. A canary takes about
55 to 65 minutes, and about 30 of those are the destroy. Most of the time goes on slow platform
resources (the Container Apps environment, registry, Key Vault and role assignments, the Entra app),
not on the app.

1. **Measure first.** Read J-023's HCP apply log for per-resource create times, and the canary
   logs for the apply/smoke/destroy split. Decide what to share from those numbers, not guesses.

   **Measured 2026-11-05.** Source: J-023's `todoapp-two-j023` workspace (Postgres; J-026's was
   already deleted) and canaries 18 and 19.

   **Fresh preview, about 31 min end to end.** The two applies take about 20 min wall time; the
   rest is images, firewall and schema.
   - Apply 1 (registry and database), 10 min:
     - Postgres flexible server 304 s;
     - Key Vault 145 s;
     - Postgres `azure_services` firewall rule 78 s;
     - everything else is 23 s or less (RG, ACR, role assignments, the database).
   - Apply 2, 9.5 min:
     - **Container Apps environment 465 s**;
     - Log Analytics 44 s;
     - the AcrPull role 35 s;
     - everything else is 23 s or less, including the container app (18 s), the SWA (15 s) and
       all the Entra and Graph resources (each 13 s or less).

   **Destroy, 26 min.** The Container Apps environment's delete took **1493 s (25 min)**.
   Postgres took 63 s and everything else 24 s or less.

   **Canary, about 61 min.** Canary 19:
   - started 12:37;
   - deploy run 12:37:32 to 13:07:31 (30 min);
   - smoke seconds;
   - pass recorded at 13:38 after the destroy (30 min).

   Canary 18 had the same split (36.5 + 28.5 min).

   **So:**
   - Passing the canary before its destroy halves it (~61 to ~31 min).
   - The Container Apps environment alone is ~8 min of create and ~25 min of destroy.
   - The Postgres server, its firewall rule and Key Vault add ~9 min of create.
   - Sharing those three plus the environment would bring a fresh preview close to the ~10 min
     re-apply, and the destroy to a few minutes.
   - The Entra and Graph resources are cheap (13 s or less each) and can stay per preview.

   **Leak found:** J-023's destroy errored on 11-02 and nothing retried it.
   - The error was a redirect-URI delete that 404ed because the Entra app was already gone.
   - The board still says `up`, and `rg-todoapp-j023` still holds its SWA.
   - Step 2 should make the `destroyAt` sweep retry a failed destroy, and tolerate a 404 on an
     already-deleted Entra object.
2. **Factory side** (this repo, red then green as usual):
   - The canary records its pass once the smoke passes, and the destroy runs after; the `destroyAt`
     sweep catches a destroy that fails. Roughly halves the canary.
   - Two canary levels: a change to only the factory's deploy scripts re-applies a `j000` kept up;
     the full create-and-destroy canary stays for template IaC, `deploy.yml` and add-on changes.
3. **Template side** (`template-projects`, so a template job or lesson, not this repo, and it
   overlaps the deferred AVM migration): split the IaC into a long-lived platform stack and a thin
   per-preview stack.

   **Decided 2026-11-06 (Kurt), from step 1's numbers and `centralus` list prices:**
   - Shared: the Container Apps environment (no fixed charge in the docs), Log Analytics (first
     5 GB free, then $2.76/GB), Key Vault Standard (~$0) and the Postgres flexible server
     (B1ms + 32 GB, ~$18.18/month).
   - Per preview: the registry (23 s or less to create; ~$5/month if shared), App Insights, the
     storage account, the managed identity and its role assignments, the Entra External ID app.
   - The aim: a fresh preview of about 14 min, down from ~31 (up to 1036 s saved), and a destroy
     of a couple of minutes, down from 26. Both are estimates until measured.

   Check the first bill: the price list has a $0.10/hour "Environment Management Hour" meter
   (~$73/month) that the billing docs don't name or apply to a Consumption-only environment.

   What the split has to solve:
   - Database names per preview (`database-postgres` hardcodes `appdb` and `eventstore`).
   - Secret names per preview in the shared vault, and soft-delete on destroy (`j000` reuses its
     names on every canary).
   - Each preview gets its own Postgres role, so none can reach another's database. HCP's runners
     are outside Azure and can't get through the server's firewall, so this is done with
     `az postgres flexible-server execute`, not Terraform (Kurt, 2026-11-06: not everything has to
     run through HCP; use az where it is the better path, and document it). `destroy` drops the
     role the same way, so none are left behind.
   - Container app names must be unique within the shared environment, so they carry the preview id.
   - dev, staging and existing scaffolds keep their own stacks: no platform set means a full stack.
4. **Trade-off to accept:** previews share logs, the vault, the Postgres server (1 vCore, 2 GiB) and
   environment limits, and a fresh preview no longer proves a from-scratch create. The full canary
   keeps proving that.

## Not decided (ask before doing)

- Phase 7: whether a canary may pass before its destroy finishes.

- Whether reviewers should also flag out-of-scope paths. The decision was deterministic only.
- Whether the canary scaffold gets `auth: true` by default (Phase 5, step 4).
- The tweak limits (10 files / 300 lines) and the promote globs are first guesses. Revisit them
  after a few real tweaks.

## Also from J-022, already recorded and not in this plan

`.factory-runs/lessons/J-022.md` holds the template fixes, for the dashboard's Lessons tab to propose:
- the auth add-on's `identifier_uris` ignore and the scope requested by client id,
- its `auth.ts` error handling,
- its tftest mock id,
- `Auth__AdminEmail` and the `email` optional claim,
- the deploy-app skill's orphaned-resource row.
