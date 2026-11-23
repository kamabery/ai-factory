# 24: Put the preview platform to sleep, and wake it before a deploy

**Status:** planned (2026-11-22), after 22. **Branch:** `feature/24-platform-sleep`. **Depends on:** 16, 18, 22.

## Why

On 2026-11-22 spec 22's live proof hit a stopped database. The shared preview platform's Postgres flexible server
(`psql-factory-<suffix>` in `rg-factory-platform`, Burstable B1ms, about $13 a month plus storage while it runs) had
been stopped by hand twice that day to save money while idle. The next canary (run 33) then failed after three minutes
with `ServerStoppedError: Server 'psql-factory-3c2b3241' is in 'Stopped' state`, and J-039 (kamabery/recipebox) waited,
red, in the Canary column until the server was started by hand with `az`.

Stopping the server is the right way to save money: everything else on the platform costs next to nothing idle (the
Container Apps environment scales to zero, Key Vault is pennies, Log Analytics is billed per GB, and the Azure board's
own SQL database pauses itself). But nothing in the factory knows about it. Kurt (2026-11-22): **add buttons in the
factory to start Azure and put it to sleep.**

## Decisions

Grilled 2026-11-22.

| Topic | Decision | Not chosen |
| --- | --- | --- |
| What sleeps | **The platform Postgres server only**: the one platform resource with a real idle cost. | Tearing down the Container Apps environment or Key Vault; anything in `rg-factory-cloud` |
| The control | **A header chip with Start and Sleep**, shown when deploys are on: `platform: awake`, `asleep`, `starting`, `stopping`. Both are thin wrappers over a new `factory platform status\|start\|sleep`. | A separate settings page; az commands in the docs only |
| Auto-wake (Kurt) | **Yes: the factory wakes it before it needs it.** The canary and the deploy stage run `platform start` first when the server is stopped, and wait for `Ready` (a few minutes, shown as the canary's or the deploy's phase). | Buttons only, so a stopped server fails the canary or deploy |
| Sleep guard (Kurt) | **Refuse while in use**: while a canary runs, a job is in `canary` or `deploy`, or any job's preview is up (`deploy.status` `up` or `deploying`), Sleep is refused and the refusal names what holds it (`platform busy: J-039's preview is up`). | Tearing every preview down first; a warning only |
| Auto-sleep (Kurt) | **Not now.** Sleep is manual; an idle timer is a follow-up. | The watchdog sleeping it after N idle hours |
| Where (Kurt) | **Its own spec, after 22**, on its own branch. | Part 4 of spec 22 |

## Scope

Contract first (`docs/FACTORY-CONTRACT.md`, "Deploy", "Canary", "Dashboard API", "factory commands"):

- **`factory platform status|start|sleep`** (deploys only): finds the server from the previews setup's settings (the
  platform resource group and server name; `previews-env` writes them to `.env` if they are not there yet), and calls
  `az postgres flexible-server show|start|stop` (contract: az where it fits better than HCP Terraform, as for Postgres
  roles). `status` prints `awake`, `asleep`, `starting` or `stopping`. `start` is a no-op when awake and waits for
  `Ready`. `sleep` applies the guard above, then stops it and returns without waiting. Refusals as usual.
- **Auto-wake:** `canary` (a new first phase, `wake`, only when the server is not `Ready`) and `deploy` (before the
  attempt, recorded on the job's deploy as `waking`) run `platform start`. An attempt that fails while waking counts
  like any failed attempt.
- **Dashboard API:** `GET /api/platform` (`platform status`, cached 30 s), `POST /api/platform/start` (started
  detached; answers at once) and `POST /api/platform/sleep` (refusal shown sticky, like a refused approval).
- **SPA:** a `PlatformChip` beside the canary due chip, with **Start** when asleep and **Sleep** when awake; `starting`
  and `stopping` show the moving marker. The canary banner's phase list gains `waking the platform`.
- **Cloud board:** its runner job's managed identity needs start/stop on the server (a role assignment scoped to the
  server in `factory/infra/cloud`, test-first), and the cloud dashboard gets the same chip.
- **Docs:** DEPLOY.md ("The shared platform": what sleeps, what it saves, the chip, auto-wake), CLOUD.md (the role),
  TERMINOLOGY ("Platform").

## Tests

Seen red before green, per `docs/TESTING.md`:

- Unit: `platform status` maps each az state; `start` is a no-op when awake and waits for `Ready`; `sleep` is refused
  for each guard case (canary running, a job in `canary`, a job in `deploy`, a preview `up` or `deploying`) with the
  holder named, and stops otherwise; no `platform` with deploys off; the canary's `wake` phase only when not `Ready`; a
  deploy wakes first; az errors as refusals.
- Server: the three endpoints and the 30 s cache. Vitest: the chip in each state, its buttons, a sticky refusal.
- Playwright: the chip against a stubbed `az` (`FACTORY_AZ_CMD`, as the deploy tests already do).
- Terraform: `factory/infra/cloud`'s role assignment, `terraform test`.
- Live, on the local board: Sleep the platform, see the chip say asleep, approve a deployable job, and see the canary
  or deploy wake it and the job preview. Cloud: the `@cloud` check sees the chip.

## Done when

The header shows whether the preview platform is awake and has Start and Sleep; Sleep is refused while anything uses
it and says what; a canary or a deploy wakes a sleeping platform by itself instead of failing; the cloud board can do
the same; and DEPLOY.md says what sleeps and what it saves.

## Follow-ups

- **Auto-sleep** after an idle time with nothing using the platform (no preview up, no canary, nothing in Canary or
  deploy), by the dashboard's watchdog.
