# 22: Show the canary while it runs, and build beside it

**Status:** planned (2026-11-20), after 18. **Branch:** `feature/22-canary-visibility`. **Depends on:** 17, 18.

## Why

On 2026-11-20 the cloud factory's first deployable job after the 0.3.8 release (J-003, kamabery/booklist) sat in
Approved for over 10 minutes with no sign of why. The runner was running the 0.3.8 canary first, as the contract's
"The gate" says, but the dashboard showed nothing. `CanaryBanner` shows "Deploy canary running" only when
`canary.json` says `running`. In cloud mode the canary runs in the runner's container, and `canary.json` reached
Blob only when the canary finished, so the dashboard never saw the `running` state. Before the job was approved,
nothing said a canary was due either: the canary is lazy, and **Run canary** only appears after a failure.

## Decisions (grilled 2026-11-20)

| Topic | Decision | Not chosen |
| --- | --- | --- |
| Where it shows | **The existing top banner**, made to work in cloud mode. | A note on the waiting job's card; the job's modal; spec 21's session stepper |
| Detail | **Phase and link**: `Deploy canary (0.3.8) · deploying 2/3 · 7 of ~15 min · Actions run ↗`, and a line naming the jobs waiting for it. Phases: scaffold → deploying 1/3, 2/3, 3/3 → smoke → sign-in → tearing down. The ~15 min comes from recent passes. | Running and elapsed time only; a live log panel |
| Plumbing | **The canary writes `canary.json` at start and at each phase change, and pushes it** (blob-backed state), not only at the end. Every board reads the same file, local or cloud. | The server reading GitHub Actions directly (it sees only the deploy, not smoke, sign-in or teardown) |
| Due | **A quiet "canary due" chip** once the fingerprint has no pass: "canary due: the next deployable job runs it beside its build (~15 min)". | Saying nothing until it runs; running it eagerly after every release |
| The gate (added 2026-11-20) | **The canary runs in parallel with the build.** A deployable job no longer waits in Approved: the runner moves it to Build and starts the agent build and the canary together. The job's card shows `canary running 7/~15m` under its build status. Only the **deploy step** waits for the pass. If the canary fails, the job blocks at deploy with the canary's error; **Run canary**, then **Retry deploy**, carries on. Saves about 15 min per release, at the risk of a paid build whose deploy then waits. | Canary first inside Build (the build waits for the pass); today's wait in Approved |

## Scope

Contract first (`docs/FACTORY-CONTRACT.md`, "Canary" and "Dashboard API"):

- `canary.json` gains `phase` (`scaffold` | `deploy-1` | `deploy-2` | `deploy-3` | `smoke` | `signin` | `teardown`,
  null when finished), `version` (the factory package version the fingerprint names, for the banner), and `run`
  (the deploy workflow run's URL, once dispatched).
- The canary saves `canary.json` at start and at each phase change. With shared files (`azure`, or `sql` with
  `FACTORY_BLOB_URL`), each save pushes `canary.json` right away, not at the end of the command.
- **Due:** `GET /api/canary` (or the board's existing canary read) adds `due: true` when `canary.json` has no pass for
  the current fingerprint and no canary is running. The SPA shows the chip; it disappears once a canary starts.
- **Typical duration:** the median of the last passes' `finishedAt - startedAt`, from `canary.json` history (or a
  fixed 15 min until there are three passes). Show it as "N of ~M min".
- **The gate** ("Canary", "The gate"): `next` returns a deployable job even without a pass for the current
  fingerprint. When it does, the runner starts `canary` as a detached command in the same execution, then runs the
  job. `deploy` waits for the canary to finish, polling `canary.json`: on a pass it goes ahead, and on a failure the
  job goes to `blocked` with `canary failed: <error>`. One canary per fingerprint: a second deployable job taken
  meanwhile waits on the same run. The container already outlives detached commands (0.3.4).
- **Card indicator:** a job whose deploy waits on a running or due canary shows a chip under its status:
  `canary running N/~M min`, or `canary failed: deploy waits` with a link to the banner.
- **SPA:** `CanaryBanner` renders the running state from the new fields, and the jobs waiting for it are the
  deployable jobs whose deploy waits on it. A `CanaryDueChip` sits by the run-mode chip.

## Tests

Seen red before green, per `docs/TESTING.md`:

- Unit: `next` returns a deployable job with no pass; the runner starts the canary detached, once per fingerprint; `deploy` waits for a pass, and blocks on a failure; canary phase saves in order; each pushes when files are shared and doesn't when they're not; `due` for
  pass/failed/running/none against the fingerprint; the typical-duration rule.
- Vitest: the banner for each phase (text, elapsed time, link, waiting jobs) and the due chip.
- Playwright `@smoke`: the Simulator writes a running canary with a phase, and the banner shows it.
- Live: after the next image release, the cloud banner shows the phases while the canary runs.

## Done when

Approving the first deployable job after a release moves it straight to Build, with the canary running beside it, and shows the canary's phase, elapsed time and Actions link in the
banner within a minute of the canary starting, in both local and cloud modes. The "canary due" chip is visible
before anything is approved.
